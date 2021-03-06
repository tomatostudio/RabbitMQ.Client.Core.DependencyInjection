using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client.Core.DependencyInjection.Configuration;
using RabbitMQ.Client.Core.DependencyInjection.Filters;
using RabbitMQ.Client.Core.DependencyInjection.Models;
using RabbitMQ.Client.Core.DependencyInjection.Services;
using RabbitMQ.Client.Core.DependencyInjection.Services.Interfaces;
using RabbitMQ.Client.Core.DependencyInjection.Tests.Stubs;
using RabbitMQ.Client.Events;
using Xunit;

namespace RabbitMQ.Client.Core.DependencyInjection.Tests.UnitTests
{
    public class BaseBatchMessageHandlerTests
    {
        private readonly TimeSpan _globalTestsTimeout = TimeSpan.FromSeconds(60);
        
        [Theory]
        [InlineData(1, 10)]
        [InlineData(5, 47)]
        [InlineData(10, 185)]
        [InlineData(16, 200)]
        [InlineData(20, 310)]
        [InlineData(25, 400)]
        public async Task ShouldProperlyHandlerMessagesByBatches(ushort prefetchCount, int numberOfMessages)
        {
            const string queueName = "queue.name";

            var channelMock = new Mock<IModel>();
            var connectionMock = new Mock<IConnection>();
            connectionMock.Setup(x => x.CreateModel())
                .Returns(channelMock.Object);

            var connectionFactoryMock = new Mock<IRabbitMqConnectionFactory>();
            connectionFactoryMock.Setup(x => x.CreateRabbitMqConnection(It.IsAny<RabbitMqServiceOptions>()))
                .Returns(connectionMock.Object);

            var consumer = new AsyncEventingBasicConsumer(channelMock.Object);
            connectionFactoryMock.Setup(x => x.CreateConsumer(It.IsAny<IModel>()))
                .Returns(consumer);

            var callerMock = new Mock<IStubCaller>();

            using var messageHandler = CreateBatchMessageHandler(
                queueName,
                prefetchCount,
                null,
                connectionFactoryMock.Object,
                callerMock.Object,
                Enumerable.Empty<IBatchMessageHandlingFilter>());
            await messageHandler.StartAsync(CancellationToken.None);

            for (var i = 0; i < numberOfMessages; i++)
            {
                await consumer.HandleBasicDeliver(
                    "1",
                    (ulong)i,
                    false,
                    "exchange",
                    "routing,key",
                    null,
                    new ReadOnlyMemory<byte>());
            }

            var numberOfBatches = numberOfMessages / prefetchCount;
            callerMock.Verify(x => x.EmptyCall(), Times.Exactly(numberOfBatches));

            var processedMessages = numberOfBatches * prefetchCount;
            callerMock.Verify(x => x.Call(It.IsAny<ReadOnlyMemory<byte>>()), Times.Exactly(processedMessages));

            await messageHandler.StopAsync(CancellationToken.None);
        }
        
        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(10)]
        [InlineData(16)]
        [InlineData(40)]
        [InlineData(57)]
        public async Task ShouldProperlyHandlerMessagesByTimer(int numberOfMessages)
        {
            const string queueName = "queue.name";
            const ushort prefetchCount = 10;
            var handlingPeriod = TimeSpan.FromMilliseconds(100);

            var channelMock = new Mock<IModel>();
            var connectionMock = new Mock<IConnection>();
            connectionMock.Setup(x => x.CreateModel())
                .Returns(channelMock.Object);

            var connectionFactoryMock = new Mock<IRabbitMqConnectionFactory>();
            connectionFactoryMock.Setup(x => x.CreateRabbitMqConnection(It.IsAny<RabbitMqServiceOptions>()))
                .Returns(connectionMock.Object);

            var consumer = new AsyncEventingBasicConsumer(channelMock.Object);
            connectionFactoryMock.Setup(x => x.CreateConsumer(It.IsAny<IModel>()))
                .Returns(consumer);

            using var waitHandle = new AutoResetEvent(false);
            var callerMock = new Mock<IStubCaller>();
            var caller = new StubCallerDecorator(callerMock.Object)
            {
                WaitHandle = waitHandle
            };

            using var messageHandler = CreateBatchMessageHandler(
                queueName,
                prefetchCount,
                handlingPeriod,
                connectionFactoryMock.Object,
                caller,
                Enumerable.Empty<IBatchMessageHandlingFilter>());
            await messageHandler.StartAsync(CancellationToken.None);

            const int smallBatchSize = prefetchCount - 1;
            var numberOfSmallBatches = (int)Math.Ceiling((double)numberOfMessages / smallBatchSize);
            for (var b = 0; b < numberOfSmallBatches; b++)
            {
                var lowerBound = b * smallBatchSize;
                var upperBound = (b + 1) * smallBatchSize > numberOfMessages ? numberOfMessages : (b + 1) * smallBatchSize;
                for (var i = lowerBound; i < upperBound; i++)
                {
                    await consumer.HandleBasicDeliver(
                        "1",
                        (ulong)i,
                        false,
                        "exchange",
                        "routing,key",
                        null,
                        new ReadOnlyMemory<byte>());
                }
                
                waitHandle.WaitOne(_globalTestsTimeout);
            }

            callerMock.Verify(x => x.EmptyCall(), Times.Exactly(numberOfSmallBatches));
            callerMock.Verify(x => x.Call(It.IsAny<ReadOnlyMemory<byte>>()), Times.Exactly(numberOfMessages));
            
            await messageHandler.StopAsync(CancellationToken.None);
        }
        
        [Fact]
        public async Task ShouldProperlyExecutePipelineInReverseOrder()
        {
            const ushort prefetchCount = 5;
            const string queueName = "queue.name";

            var channelMock = new Mock<IModel>();
            var connectionMock = new Mock<IConnection>();
            connectionMock.Setup(x => x.CreateModel())
                .Returns(channelMock.Object);

            var connectionFactoryMock = new Mock<IRabbitMqConnectionFactory>();
            connectionFactoryMock.Setup(x => x.CreateRabbitMqConnection(It.IsAny<RabbitMqServiceOptions>()))
                .Returns(connectionMock.Object);

            var consumer = new AsyncEventingBasicConsumer(channelMock.Object);
            connectionFactoryMock.Setup(x => x.CreateConsumer(It.IsAny<IModel>()))
                .Returns(consumer);

            var callerMock = new Mock<IStubCaller>();

            var handlerOrderMap = new Dictionary<int, int>();
            var firstFilter = new StubBatchMessageHandlingFilter(1, handlerOrderMap);
            var secondFilter = new StubBatchMessageHandlingFilter(2, handlerOrderMap);
            var thirdFilter = new StubBatchMessageHandlingFilter(3, handlerOrderMap);
            
            var handlingFilters = new List<IBatchMessageHandlingFilter>
            {
                firstFilter,
                secondFilter,
                thirdFilter
            };

            using var messageHandler = CreateBatchMessageHandler(
                queueName,
                prefetchCount,
                null,
                connectionFactoryMock.Object,
                callerMock.Object,
                handlingFilters);
            await messageHandler.StartAsync(CancellationToken.None);

            for (var i = 0; i < prefetchCount; i++)
            {
                await consumer.HandleBasicDeliver(
                    "1",
                    (ulong)i,
                    false,
                    "exchange",
                    "routing,key",
                    null,
                    new ReadOnlyMemory<byte>());
            }

            callerMock.Verify(x => x.EmptyCall(), Times.Once);
            Assert.Equal(1, handlerOrderMap[thirdFilter.MessageHandlerNumber]);
            Assert.Equal(2, handlerOrderMap[secondFilter.MessageHandlerNumber]);
            Assert.Equal(3, handlerOrderMap[firstFilter.MessageHandlerNumber]);
            
            await messageHandler.StopAsync(CancellationToken.None);
        }

        private static BaseBatchMessageHandler CreateBatchMessageHandler(
            string queueName,
            ushort prefetchCount,
            TimeSpan? handlingPeriod,
            IRabbitMqConnectionFactory connectionFactory,
            IStubCaller caller,
            IEnumerable<IBatchMessageHandlingFilter> handlingFilters)
        {
            var connectionOptions = new BatchConsumerConnectionOptions
            {
                Type = typeof(StubBaseBatchMessageHandler),
                ServiceOptions = new RabbitMqServiceOptions()
            };
            var loggerMock = new Mock<ILogger<StubBaseBatchMessageHandler>>();
            return new StubBaseBatchMessageHandler(
                caller,
                connectionFactory,
                new List<BatchConsumerConnectionOptions> { connectionOptions },
                handlingFilters,
                loggerMock.Object)
            {
                QueueName = queueName,
                PrefetchCount = prefetchCount,
                MessageHandlingPeriod = handlingPeriod
            };
        }
    }
}