using System;
using Microsoft.Owin.Hosting;

namespace TestSimpleApp.AWSSDK.Framework
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            const string baseAddress = "http://localhost:9000";
            using (WebApp.Start<Startup>(baseAddress))
            {
                Console.WriteLine($"Web app started at {baseAddress}. Press ENTER to stop");
                Console.ReadLine();
            }
        }
    }
}