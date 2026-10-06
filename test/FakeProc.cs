// 测试替身：模拟 frpc 的输出行为（UTF-8 字节直写标准流）。
using System;
using System.IO;
using System.Text;
using System.Threading;

static class FakeProc
{
    static void Main()
    {
        Stream so = Console.OpenStandardOutput();
        Stream se = Console.OpenStandardError();
        int i = 0;
        while (true)
        {
            string lvl = (i % 10 == 3) ? "W" : (i % 10 == 7 ? "E" : "I");
            Emit(so, string.Format("{0} [{1}] [fake.go:{2}] tick {3} 中文测试 ok",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), lvl, i % 99, i));
            if (i % 5 == 0)
                Emit(se, string.Format("{0} [E] [fake.go:1] stderr tick {1}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), i));
            i++;
            Thread.Sleep(300);
        }
    }

    static void Emit(Stream s, string line)
    {
        byte[] b = Encoding.UTF8.GetBytes(line + "\n");
        s.Write(b, 0, b.Length);
        s.Flush();
    }
}
