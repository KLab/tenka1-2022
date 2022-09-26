using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace game
{
    internal static class Program
    {
        const int EXIT_CODE_RESTART = 64;

        static void Test()
        {
            var a = new int[294 * 6];
            var rnd = new Random();
            var sw = new Stopwatch();
            sw.Restart();
            for (var tt = 0; tt < 1000; ++tt)
            {
                for (var i = 0; i < a.Length; i++)
                {
                    a[i] = rnd.Next() % 4;
                }

                var gameLogic = new GameLogic(0, a);
                gameLogic.GetResponseData(0);
            }
            Console.WriteLine($"{sw.Elapsed}");
        }

        public static async Task Main(string[] args)
        {
            var calcScoreMode = true;
            var apiMode = true;
            var batchMode = false;
            var startAt = 0L;
            var endAt = long.MaxValue;
            if (args.Length >= 1)
            {
                switch (args[0])
                {
                    case "calcScore":
                        apiMode = false;
                        break;
                    case "api":
                        calcScoreMode = false;
                        break;
                    case "batch":
                        apiMode = false;
                        calcScoreMode = false;
                        batchMode = true;
                        break;
                    case "test":
                        Test();
                        return;
                    case "-":
                        break;
                    default:
                        throw new Exception("invalid args[0] (mode)");
                }
            }

            if (args.Length >= 2)
            {
                var a = args[1].Split("/");
                Debug.Assert(a.Length == 2);
                startAt = long.Parse(a[0]);
                endAt = startAt + long.Parse(a[1]);
            }

            var gameDbHost = Environment.GetEnvironmentVariable("GAMEDB_HOST") ?? "localhost";
            var gameDbPort = Environment.GetEnvironmentVariable("GAMEDB_PORT") ?? "6379";
            var redisConfig = ConfigurationOptions.Parse($"{gameDbHost}:{gameDbPort}");
            var redis = ConnectionMultiplexer.Connect(redisConfig);

            var cts = new CancellationTokenSource();
            if (apiMode)
            {
                var apiPort = Environment.GetEnvironmentVariable("API_PORT") ?? "8080";
                new Api(redis, redisConfig, apiPort, startAt, endAt).Start(cts.Token);
            }

            Console.WriteLine("started.");

            if (calcScoreMode)
            {
                new Thread(() => new CalcScore(redis, redisConfig, startAt, endAt).Start(cts.Token)).Start();
            }

            if (!batchMode)
            {
                redis.GetSubscriber().Subscribe("restart", (_, message) => {
                    var msg = (string)message;
                    if (msg == "all" || (apiMode && msg == "api") || (calcScoreMode && msg == "calcScore")) {
                        System.Environment.ExitCode = EXIT_CODE_RESTART;
                        cts.Cancel();
                    }
                });
                try {
                    await Task.Delay(Timeout.Infinite, cts.Token);
                } catch (TaskCanceledException) { /* ignored */ }
            }
        }
    }
}
