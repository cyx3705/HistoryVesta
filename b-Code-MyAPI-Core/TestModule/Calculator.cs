using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TestModule
{
    public class Calculator
    {
        // 同步方法
        public int Add(int a, int b)
        {
            return a + b;
        }

        // 异步方法
        public async Task<string> SayHello(string name)
        {
            await Task.Delay(100);
            return $"Hello {name} (from .NET Framework)";
        }
    }
}
