using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScalableGameServer
{
    class Program
    {
        static void Main(string[] args)
        {
            IOCPServer server = new IOCPServer();
            server.Start();
        }
    }
}
