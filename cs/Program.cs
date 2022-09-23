using System.Text.Json;
using System.Text.Json.Serialization;

namespace cs
{
    internal class Program
    {
        private static readonly HttpClient Client = new HttpClient();

        private readonly string _gameServer;
        private readonly string _token;

        private const int N = 5;
        private static readonly int[] Dj = {+1, 0, -1, 0};
        private static readonly int[] Dk = {0, +1, 0, -1};

        // ゲームサーバのAPIを叩く
        private async Task<byte[]> CallApi(string x)
        {
            var url = $"{_gameServer}{x}";
            // 5xxエラーの際は100ms空けて5回までリトライする
            for (var i = 0; i < 5; i++)
            {
                Console.WriteLine(url);
                var res = await Client.GetAsync(url);

                if (500 <= (int)res.StatusCode && (int)res.StatusCode < 600) {
                    Console.WriteLine($"{res.StatusCode}");
                    Thread.Sleep(100);
                    continue;
                }

                return await res.Content.ReadAsByteArrayAsync();
            }
            throw new Exception("Api Error");
        }

        // 指定したmode, delayで練習試合開始APIを呼ぶ
        async Task<Start> CallStart(int mode, int delay)
        {
            var json = await CallApi($"/api/start/{_token}/{mode}/{delay}");
            return JsonSerializer.Deserialize<Start>(json);
        }

        // dir方向に移動するように移動APIを呼ぶ
        async Task<Move> CallMove(int gameId, int dir)
        {
            var json = await CallApi($"/api/move/{_token}/{gameId}/{dir}");
            return JsonSerializer.Deserialize<Move>(json);
        }

        // game_idを取得する
        // 環境変数で指定されていない場合は練習試合のgame_idを返す
        async Task<int> GetGameId()
        {
            // 環境変数にGAME_IDが設定されている場合これを優先する
            var envGameId = Environment.GetEnvironmentVariable("GAME_ID");
            if (envGameId != null)
            {
                return int.Parse(envGameId);
            }
            
            // start APIを呼び出し練習試合のgame_idを取得する
            var start = await CallStart(0, 0);
            if (start.Status == "ok" || start.Status == "started")
            {
                return start.GameId;
            }

            throw new Exception($"Start Api Error : {start.Status}");
        }

        // BOTのメイン処理
        private async Task Solve()
        {
            var random = new Random();
            var gameId = await GetGameId();
            var nextD = random.Next(4);
            for (;;)
            {
                // 移動APIを呼ぶ
                var move = await CallMove(gameId, nextD);
                Console.WriteLine($"status = {move.Status}");
                if (move.Status == "already_moved")
                {
                    continue;
                }
                else if (move.Status != "ok")
                {
                    break;
                }
                Console.WriteLine($"turn = {move.Turn}");
                Console.WriteLine($"score = {move.Score[0]} {move.Score[1]} {move.Score[2]} {move.Score[3]} {move.Score[4]} {move.Score[5]}");
                // 4方向で移動した場合を全部シミュレーションする
                var bestC = -1;
                var bestD = new List<int>();
                for (var d = 0; d < 4; d++)
                {
                    var m = new State(move.Field, move.Agent);
                    m.Move(new[] { d, -1, -1, -1, -1, -1 });
                    // 自身のエージェントで塗られているマス数をカウントする
                    var c = 0;
                    for (var i = 0; i < 6; i++)
                    {
                        for (var j = 0; j < N; j++)
                        {
                            for (var k = 0; k < N; k++)
                            {
                                if (m.Field[i][j][k][0] == 0) c++;
                            }
                        }
                    }

                    // 最も多くのマスを自身のエージェントで塗れる移動方向のリストを保持する
                    if (c > bestC)
                    {
                        bestC = c;
                        bestD.Clear();
                        bestD.Add(d);
                    }
                    else if (c == bestC)
                    {
                        bestD.Add(d);
                    }
                }

                // 最も多くのマスを自身のエージェントで塗れる移動方向のリストからランダムで方向を決める
                nextD = bestD[random.Next(bestD.Count)];
            }
        }

        private Program()
        {
            _gameServer = Environment.GetEnvironmentVariable("GAME_SERVER") ?? "https://2022contest.gbc.tenka1.klab.jp";
            _token = Environment.GetEnvironmentVariable("TOKEN") ?? "YOUR_TOKEN";
        }

        private static async Task Main(string[] args)
        {
            await new Program().Solve();
        }

        // ゲーム状態クラス
        private class State
        {
            public int[][][][] Field;

            public int[][] Agent;

            public State(int[][][][] field, int[][] agent)
            {
                Field = JsonSerializer.Deserialize<int[][][][]>(JsonSerializer.SerializeToUtf8Bytes(field))!;
                Agent = JsonSerializer.Deserialize<int[][]>(JsonSerializer.SerializeToUtf8Bytes(agent))!;
            }

            // idxのエージェントがいる位置のFieldを更新する
            private void Paint(int idx)
            {
                var i = Agent[idx][0];
                var j = Agent[idx][1];
                var k = Agent[idx][2];
                if (Field[i][j][k][0] == -1)
                {
                    // 誰にも塗られていない場合はidxのエージェントで塗る
                    Field[i][j][k][0] = idx;
                    Field[i][j][k][1] = 2;
                }
                else if (Field[i][j][k][0] == idx)
                {
                    // idxのエージェントで塗られている場合は完全に塗られた状態に上書きする
                    Field[i][j][k][1] = 2;
                }
                else if (Field[i][j][k][1] == 1)
                {
                    // idx以外のエージェントで半分塗られた状態の場合は誰にも塗られていない状態にする
                    Field[i][j][k][0] = -1;
                    Field[i][j][k][1] = 0;
                }
                else
                {
                    // idx以外のエージェントで完全に塗られた状態の場合は半分塗られた状態にする
                    Field[i][j][k][1] -= 1;
                }
            }

            // エージェントidxをd方向に回転させる
            // 方向については問題概要に記載しています
            private void RotateAgent(int idx, int d)
            {
                Agent[idx][3] += d;
                Agent[idx][3] %= 4;
            }

            // idxのエージェントを前進させる
            // マス(i, j, k)については問題概要に記載しています
            private void MoveForward(int idx)
            {
                var i = Agent[idx][0];
                var j = Agent[idx][1];
                var k = Agent[idx][2];
                var d = Agent[idx][3];
                var jj = j + Dj[d];
                var kk = k + Dk[d];
                if (jj >= N)
                {
                    Agent[idx][0] = i / 3 * 3 + (i % 3 + 1) % 3;  // [1, 2, 0, 4, 5, 3][i]
                    Agent[idx][1] = k;
                    Agent[idx][2] = N - 1;
                    Agent[idx][3] = 3;
                }
                else if (jj < 0)
                {
                    Agent[idx][0] = (1 - i / 3) * 3 + (4 - i % 3) % 3;  // [4, 3, 5, 1, 0, 2][i]
                    Agent[idx][1] = 0;
                    Agent[idx][2] = N - 1 - k;
                    Agent[idx][3] = 0;
                }
                else if (kk >= N)
                {
                    Agent[idx][0] = i / 3 * 3 + (i % 3 + 2) % 3;  // [2, 0, 1, 5, 3, 4][i]
                    Agent[idx][1] = N - 1;
                    Agent[idx][2] = j;
                    Agent[idx][3] = 2;
                }
                else if (kk < 0)
                {
                    Agent[idx][0] = (1 - i / 3) * 3 + (3 - i % 3) % 3;  // [3, 5, 4, 0, 2, 1][i]
                    Agent[idx][1] = N - 1 - j;
                    Agent[idx][2] = 0;
                    Agent[idx][3] = 1;
                }
                else
                {
                    Agent[idx][1] = jj;
                    Agent[idx][2] = kk;
                }
            }

            // エージェントが同じマスにいるかを判定する
            private static bool IsSamePos(int[] a, int[] b)
            {
                return a[0] == b[0] && a[1] == b[1] && a[2] == b[2];
            }

            // idxのエージェントがいるマスが自分のエージェントで塗られているかを判定する
            private bool IsOwnedCell(int idx)
            {
                var i = Agent[idx][0];
                var j = Agent[idx][1];
                var k = Agent[idx][2];
                return Field[i][j][k][0] == idx;
            }

            // 全エージェントの移動方向の配列を受け取り移動させてフィールドを更新する
            // -1の場合は移動させません(0~3は移動APIのドキュメント記載と同じです)
            public void Move(IReadOnlyList<int> move)
            {
                // エージェントの移動処理
                for (var idx = 0; idx < 6; idx++)
                {
                    if (move[idx] == -1) continue;
                    RotateAgent(idx, move[idx]);
                    MoveForward(idx);
                }

                // フィールドの更新処理
                for (var idx = 0; idx < 6; idx++)
                {
                    if (move[idx] == -1) continue;
                    var ok = true;
                    for (var j = 0; j < 6; j++)
                    {
                        if (idx == j || move[j] == -1 || !IsSamePos(Agent[idx], Agent[j]) || IsOwnedCell(idx)) continue;
                        // 移動した先にidx以外のエージェントがいる場合は修復しか行えないのでidxのエージェントのマスではない場合は更新しないようにフラグをfalseにする
                        ok = false;
                        break;
                    }

                    if (!ok) continue;
                    Paint(idx);
                }
            }
        }
    }

    // 練習試合開始APIのレスポンス用の構造体
    internal readonly struct Start
    {
        [JsonPropertyName("status")]
        public string Status { get; }
        [JsonPropertyName("game_id")]
        public int GameId { get; }
        [JsonConstructor]
        public Start(string status, int gameId) => (Status, GameId) = (status, gameId);
    }

    // 移動APIのレスポンス用の構造体
    internal readonly struct Move
    {
        [JsonPropertyName("status")]
        public string Status { get; }
        [JsonPropertyName("now")]
        public long Now { get; }
        [JsonPropertyName("turn")]
        public int Turn { get; }
        [JsonPropertyName("move")]
        public int[] Moves { get; }
        [JsonPropertyName("score")]
        public int[] Score { get; }
        [JsonPropertyName("field")]
        public int[][][][] Field { get; }
        [JsonPropertyName("agent")]
        public int[][] Agent { get; }
        [JsonConstructor]
        public Move(string status, long now, int turn, int[] moves, int[] score, int[][][][] field, int[][] agent)
            => (Status, Now, Turn, Moves, Score, Field, Agent) = (status, now, turn, moves, score, field, agent);
    }
}
