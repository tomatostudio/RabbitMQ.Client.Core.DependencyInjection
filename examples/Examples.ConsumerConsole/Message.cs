using System.Collections.Generic;

namespace Examples.ConsumerConsole
{
    public class Message
    {
        public string Name { get; set; }

        public bool Flag { get; set; }

        public int Index { get; set; }

        public IEnumerable<int> Numbers { get; set; }
    }
}