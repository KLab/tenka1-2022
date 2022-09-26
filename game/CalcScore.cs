using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using StackExchange.Redis;

namespace game
{
    internal class CalcScore
    {
        private readonly ConnectionMultiplexer _redis;
        private readonly long _startAt;
        private readonly long _endAt;
        private readonly LoadedLuaScript _getUsersScript;
        private readonly LoadedLuaScript _getGameScoreScript;
        private readonly LoadedLuaScript _addGamesScript;

        private static readonly int[,,] FinalMatching = {
            {{3,5,6,2,1,7}, {6,1,5,2,4,0}, {4,5,1,7,0,3}, {0,6,4,7,3,2}},
            {{6,1,2,4,3,5}, {3,7,4,1,6,0}, {7,0,2,5,1,6}, {3,4,5,0,7,2}},
            {{5,1,7,2,4,6}, {2,4,1,7,3,0}, {2,3,1,5,6,0}, {3,5,4,7,0,6}},
            {{2,3,0,7,5,6}, {1,0,2,4,7,5}, {4,6,7,3,2,1}, {5,3,0,4,6,1}},
            {{2,0,5,3,1,4}, {5,6,0,4,1,7}, {7,3,5,4,6,2}, {1,2,7,3,6,0}},
            {{7,4,6,3,1,5}, {3,2,0,4,6,1}, {5,1,7,3,0,2}, {0,5,2,6,7,4}},
            {{0,2,5,6,3,4}, {2,4,6,7,1,0}, {1,6,7,5,0,3}, {3,7,4,5,2,1}},
        };

        private static readonly Random RandomObj = new();

        private const int GamePeriod = GameLogic.TotalTurn * GameLogic.TurnPeriod;
        private const int IntervalPeriod = 3000;
        private const int MatchPeriod = GamePeriod + IntervalPeriod;
        private const int NumPlayFinal = 28;

        public CalcScore(ConnectionMultiplexer redis, ConfigurationOptions redisConfig, long startAt, long endAt)
        {
            _redis = redis;
            _startAt = startAt;
            _endAt = endAt;
            Debug.Assert(_startAt % MatchPeriod == 0);
            Debug.Assert(_endAt % MatchPeriod == 0 || _endAt == long.MaxValue);

            var getUsersPrepared = LuaScript.Prepare(@"
local t2 = tonumber(@t2)
redis.call('set', 'join_t2', t2)
local join_user_ids = redis.call('hgetall', 'join_pool_1')
if redis.call('hlen', 'join_pool_2') == 0 then
  redis.call('del', 'join_pool_1')
else
  redis.call('rename', 'join_pool_2', 'join_pool_1')
end
if redis.call('get', 'stop_matching') then
  join_user_ids = {}
end
return join_user_ids
");
            var getGameScorePrepared = LuaScript.Prepare(@"
local result = {}
for _, game_id in ipairs(redis.call('smembers', 'running_game_ids')) do
  local game_score_str = redis.call('hget', 'game_score', game_id)
  if game_score_str then
    redis.call('srem', 'running_game_ids', game_id)
    table.insert(result, game_score_str)
    table.insert(result, redis.call('hget', 'game_member', game_id))
  end
end
return {result, redis.call('scard', 'running_game_ids')}
");
            var addGamesPrepared = LuaScript.Prepare(@"
local t0 = tonumber(@t0)
local ranking_time = tonumber(@tt)
local matches_count = tonumber(@matchesCount)
local matches = @matches
local user_ranking = @userRanking
local class_point = @classPoint
local final_idx = tonumber(@finalIdx)
local exit_user_ids = @exit
local i = 0
redis.call('del', 'running_game_ids')
if final_idx == 0 then
  redis.call('del', 'final_game_ids')
end
local game_ids_key = 'game_ids_'..tostring(ranking_time)
for m in string.gmatch(matches, '[^!]+') do
  local start_time = math.floor(t0 + i * "+GameLogic.TurnPeriod+@" / matches_count)
  local finish_time = start_time + "+GamePeriod+@"
  local game_id = redis.call('incr', 'game_id_counter')
  redis.call('sadd', 'running_game_ids', game_id)
  redis.call('rpush', game_ids_key, game_id)
  redis.call('rpush', 'observed_game_ids', game_id)
  redis.call('hset', 'start_time', game_id, start_time)
  redis.call('hset', 'game_member', game_id, m)
  for user_id in string.gmatch(m, '[^/]+') do
    if user_id ~= '#' then
      redis.call('rpush', 'j_game_id_list_'..user_id, game_id, finish_time)
      redis.call('publish', user_id, 'SJ'..tostring(game_id))
    end
  end
  redis.call('rpush', 'j_game_id_list_"+Api.AdminUserId+@"', game_id, finish_time)
  if final_idx >= 0 then
    redis.call('rpush', 'final_game_ids', game_id)
  end
  i = i + 1
end
if ranking_time < 0 then
  redis.call('set', 'final_ranking', user_ranking)
else
  local ranking_key = 'ranking_'..tostring(ranking_time)
  local rank = 0
  for user_id in string.gmatch(user_ranking, '[^!]+') do
    redis.call('zadd', ranking_key, rank, user_id)
    redis.call('publish', user_id, 'R'..tostring(ranking_time)..'/'..tostring(rank))
    rank = rank + 1
  end
  for user_id in string.gmatch(exit_user_ids, '[^!]+') do
    redis.call('publish', user_id, 'R'..tostring(ranking_time)..'/-1')
  end
  redis.call('publish', '"+Api.AdminUserId+@"', 'R'..tostring(ranking_time)..'/0')
  local cp_key1 = 'rank_class_'..tostring(ranking_time)
  local cp_key2 = 'class_point_'..tostring(ranking_time)
  for cp in string.gmatch(class_point, '[^!]+') do
    local a = {}
    for x in string.gmatch(cp, '[^ ]+') do
      table.insert(a, x)
    end
    redis.call('hset', cp_key1, a[1], a[2])
    redis.call('hset', cp_key2, a[1], a[3])
  end
  redis.call('rpush', 'ranking_times', ranking_time)
end
return {i}
");
            Debug.Assert(redisConfig.EndPoints.Count == 1);
            var server = redis.GetServer(redisConfig.EndPoints[0]);
            _getUsersScript = getUsersPrepared.Load(server);
            _getGameScoreScript = getGameScorePrepared.Load(server);
            _addGamesScript = addGamesPrepared.Load(server);
        }

        public void Start(CancellationToken cts)
        {
            var db = _redis.GetDatabase();
            var sw = new Stopwatch();

            var userData = new Dictionary<string, UserData>();
            var userRanking = new List<string>();
            var rankingTime = _startAt - MatchPeriod;
            if (_startAt == 0)
            {
                rankingTime = Api.GetTime() / MatchPeriod * MatchPeriod;
            }

            var finalIdx = -1;
            while (!cts.IsCancellationRequested)
            {
                var t3 = rankingTime + GamePeriod;
                while (true)
                {
                    var t = Api.GetTime();
                    if (t >= t3) break;
                    Thread.Sleep(Math.Min(1000, (int)(t3 - t)));
                    if (cts.IsCancellationRequested) return;
                }

                while (true)
                {
                    var r = (RedisResult[])_getGameScoreScript.Evaluate(db);
                    var r0 = (RedisResult[])r[0];
                    for (var k = 0; k+1 < r0.Length; k += 2)
                    {
                        var gameScore = ((string)r0[k]).Split(' ').Select(int.Parse).ToArray();
                        var userIds = ((string)r0[k + 1]).Split('/').ToArray();
                        CalcRankPoint(userData, gameScore, userIds, finalIdx >= 0);
                    }

                    if ((int)r[1] == 0) break;
                    Thread.Sleep(1);
                    if (cts.IsCancellationRequested) return;
                }

                rankingTime += MatchPeriod;
                if (rankingTime >= _endAt)
                {
                    finalIdx = (int)((rankingTime - _endAt) / MatchPeriod);
                }

                Console.WriteLine($"matching start {rankingTime} {Api.GetTime()}");
                sw.Restart();

                var classPoint = "";
                var exit = "";
                if (finalIdx <= 0)
                {
                    var isLast = rankingTime >= _endAt - MatchPeriod;
                    (userRanking, classPoint) = CalcResult(userData, isLast);
                    var r = (RedisResult[])_getUsersScript.Evaluate(db, new { t2 = rankingTime + GamePeriod });
                    var a = new Dictionary<string, int>();
                    for (var i = 0; i+1 < r.Length; i += 2)
                    {
                        a.Add((string)r[i], (int)r[i+1]);
                    }

                    if (!isLast)
                    {
                        exit = string.Join('!', userRanking.Where(x => !a.ContainsKey(x)));
                        userRanking = userRanking.Where(x => a.ContainsKey(x)).ToList();
                    }

                    if (finalIdx < 0)
                    {
                        userRanking.AddRange(
                            a.Where(kv => !userData.ContainsKey(kv.Key))
                                .OrderBy(kv => kv.Value)
                                .Select(kv => kv.Key));
                    }

                    UpdateUserDataRank(userData, userRanking);
                }

                var matches = new List<string>();
                if (finalIdx < 0)
                {
                    var matchesList = new List<List<string>>();
                    for (var k = 0; k < 4; ++k)
                    {
                        matchesList.Add(new List<string>());
                    }

                    var i = 0;
                    for (var c = 1; i < userRanking.Count; ++c)
                    {
                        var j = ClassFunc(c);
                        MakeMatches(matchesList, userRanking.Skip(i).Take(j - i));
                        i = j;
                    }

                    foreach (var x in matchesList)
                    {
                        matches.AddRange(x);
                    }
                }
                else
                {
                    MakeFinalMatches(matches, userRanking, finalIdx);
                }

                if (finalIdx <= 0)
                {
                    _addGamesScript.Evaluate(db, new
                    {
                        t0 = rankingTime, tt = rankingTime, matchesCount = matches.Count, matches = string.Join('!', matches),
                        userRanking = string.Join('!', userRanking), classPoint, finalIdx, exit,
                    });
                }
                else
                {
                    var finalRanking = new List<string> { finalIdx.ToString() };
                    for (var i = 0; i < 8 && i < userRanking.Count; ++i)
                    {
                        var userId = userRanking[i];
                        var point = userData[userId].FinalRankPoint;
                        finalRanking.Add($"{userId}:{point}");
                    }

                    _addGamesScript.Evaluate(db, new
                    {
                        t0 = rankingTime, tt = -1L, matchesCount = matches.Count, matches = string.Join('!', matches),
                        userRanking = string.Join('/', finalRanking), classPoint = "", finalIdx, exit,
                    });

                    if (finalIdx <= NumPlayFinal + 1)
                    {
                        var gameIds = db.ListRange("final_game_ids", 0, finalIdx - 1);
                        var publishData = new List<string>
                        {
                            rankingTime.ToString(),
                            finalIdx == NumPlayFinal + 1 ? "-1" : (string)gameIds[^1],
                        };
                        var gameCounter = new Dictionary<string, int>();
                        var rankPoints = new Dictionary<string, int>();
                        for (var i = 0; i < 8 && i < userRanking.Count; ++i)
                        {
                            var userId = userRanking[i];
                            gameCounter[userId] = 0;
                            rankPoints[userId] = 0;
                        }

                        foreach (var x in gameIds[..^1])
                        {
                            var gameId = (int)x;
                            var gameScore = ((string)db.HashGet("game_score", gameId)).Split(' ').Select(int.Parse).ToArray();
                            var userIds = ((string)db.HashGet("game_member", gameId)).Split('/');
                            for (var i = 0; i < 6; i++)
                            {
                                if (userIds[i] == "#") continue;
                                var s = 6;
                                foreach (var t in gameScore)
                                {
                                    if (t == gameScore[i]) s -= 1;
                                    else if (t > gameScore[i]) s -= 2;
                                }

                                gameCounter[userIds[i]]++;
                                rankPoints[userIds[i]] += s;
                            }
                        }

                        var rankData = new List<(int, int, string)>();
                        for (var i = 0; i < 8 && i < userRanking.Count; ++i)
                        {
                            var userId = userRanking[i];
                            rankData.Add((-rankPoints[userId], i, userId));
                        }
                        rankData.Sort();
                        foreach (var (rp, _, userId) in rankData)
                        {
                            publishData.Add(userId);
                            var r = (-rp).ToString("+#;-#;0");
                            publishData.Add($"{r} / {gameCounter[userId]}");
                        }

                        db.Publish(Api.AdminUserId, "F" + string.Join('!', publishData));
                    }
                }

                Console.WriteLine($"matching end   {sw.ElapsedTicks*1e-6}");
                if (finalIdx == NumPlayFinal + 1) break;
            }
        }

        private static (List<string>, string) CalcResult(IDictionary<string, UserData> userData, bool isLast)
        {
            var rankData1 = new List<(int, int, int, string)>();
            var rankData2 = new Dictionary<int, List<(double, int, string)>>();
            var rankData3 = new List<string>();
            foreach (var (userId, v) in userData)
            {
                var rankClass = v.RankClass;
                var (classPoint1, classPoint2) = v.ClassPoint;
                if (classPoint1 != 0)
                {
                    rankData1.Add((rankClass, classPoint1, v.Rank, userId));
                }
                else
                {
                    if (!rankData2.ContainsKey(rankClass)) rankData2.Add(rankClass, new List<(double, int, string)>());
                    rankData2[rankClass].Add((classPoint2, v.Rank, userId));
                    var s = classPoint2.ToString("+0.000;-0.000;0.000");
                    rankData3.Add($"{userId} {rankClass} {s}");
                }
            }

            foreach (var c in rankData2.Keys.OrderBy(x => x))
            {
                var cur = rankData2[c];
                cur.Sort(ComparisonRankData2);
                if (isLast) continue;
                if (!rankData2.TryGetValue(c - 1, out var prev)) continue;
                var cnt = Math.Min(Math.Min(c - 1, cur.Count), prev.Count);
                if (cnt == 0) continue;

                for (var i = prev.Count - cnt; i < prev.Count; ++i)
                {
                    rankData1.Add((c, +2, i, prev[i].Item3));  // 降格
                }

                for (var i = 0; i < cnt; ++i)
                {
                    rankData1.Add((c - 1, -2, i, cur[i].Item3));  // 昇格
                }

                prev.RemoveRange(prev.Count - cnt, cnt);
                cur.RemoveRange(0, cnt);
            }

            foreach (var (c, a) in rankData2)
            {
                for (var i = 0; i < a.Count; ++i)
                {
                    rankData1.Add((c, 0, i, a[i].Item3));
                }
            }

            rankData1.Sort(ComparisonRankData1);
            return (rankData1.Select(x => x.Item4).ToList(), string.Join('!', rankData3));
        }

        private static int ComparisonRankData1((int, int, int, string) x, (int, int, int, string) y)
        {
            var r1 = x.Item1.CompareTo(y.Item1);
            if (r1 != 0) return r1;
            var r2 = x.Item2.CompareTo(y.Item2);
            if (r2 != 0) return -r2;
            return x.Item3.CompareTo(y.Item3);
        }

        private static int ComparisonRankData2((double, int, string) x, (double, int, string) y)
        {
            var r1 = x.Item1.CompareTo(y.Item1);
            return r1 != 0 ? -r1 : x.Item2.CompareTo(y.Item2);
        }

        private static void MakeMatches(List<List<string>> matchesList, IEnumerable<string> userIds0)
        {
            var userIds = userIds0.ToList();
            while (userIds.Count % 6 != 0)
            {
                userIds.Add("#");
            }

            foreach (var matches in matchesList)
            {
                var n = userIds.Count;
                for (var i = n - 1; i > 0; i--)
                {
                    var j = RandomObj.Next(i+1);
                    (userIds[i], userIds[j]) = (userIds[j], userIds[i]);
                }

                for (var i = 0; i < n; i += 6)
                {
                    var a = userIds.Skip(i).Take(6).ToList();
                    switch (a.Count(x => x != "#"))
                    {
                        case 0:
                            continue;
                        case 2:
                            var a2 = a.Where(x => x != "#").ToList();
                            a2.Insert(1, "#");
                            a2.Insert(1, "#");
                            a2.Insert(1, "#");
                            a2.Insert(1, "#");
                            a = a2;
                            break;
                        case 3:
                            var a3 = a.Where(x => x != "#").ToList();
                            a3.Add("#");
                            a3.Add("#");
                            a3.Add("#");
                            a = a3;
                            break;
                        case 4:
                            var a4 = a.Where(x => x != "#").ToList();
                            a4.Insert(0, "#");
                            a4.Add("#");
                            a = a4;
                            break;
                    }

                    matches.Add(string.Join('/', a));
                }
            }
        }

        private static void MakeFinalMatches(ICollection<string> matches, IReadOnlyList<string> userRanking, long t)
        {
            if (t >= 7) return;
            for (var i = 0; i < 4; ++i)
            {
                var a = new List<string>();
                for (var j = 0; j < 6; ++j)
                {
                    var r = FinalMatching[t, i, j];
                    a.Add(r < userRanking.Count ? userRanking[r] : "#");
                }

                matches.Add(string.Join('/', a));
            }
        }

        private static void CalcRankPoint(IDictionary<string, UserData> userData, IReadOnlyList<int> gameScore, IReadOnlyList<string> userIds, bool isFinal)
        {
            for (var i = 0; i < userIds.Count; ++i)
            {
                if (userIds[i] == "#") continue;
                var s = 6;
                foreach (var t in gameScore)
                {
                    if (t == gameScore[i]) s -= 1;
                    else if (t > gameScore[i]) s -= 2;
                }

                userData[userIds[i]].AddRankPoint(s, isFinal);
            }
        }

        private static void UpdateUserDataRank(IDictionary<string, UserData> userData, IReadOnlyList<string> userRanking)
        {
            var a = new HashSet<string>(userData.Keys);
            var c = 1;
            for (var i = 0; i < userRanking.Count; ++i)
            {
                if (i >= ClassFunc(c)) ++c;
                var userId = userRanking[i];
                if (userData.TryGetValue(userId, out var x))
                {
                    a.Remove(userId);
                    x.SetRank(c, i);
                }
                else
                {
                    var y = new UserData();
                    userData.Add(userId, y);
                    y.SetRank(c, i);
                }
            }

            foreach (var userId in a)
            {
                userData.Remove(userId);
            }
        }

        public static int ClassFunc(int c)
        {
            return c * (c + 1) * 3 + 6;
        }

        private class UserData
        {
            private readonly List<int> _rankList = new();
            private readonly List<int> _rankClassList = new();
            private readonly List<int> _rankPointList = new();

            public int FinalRankPoint { get; private set; }

            public int Rank => _rankList[^1];
            public int RankClass => _rankClassList[^1];

            public (int, double) ClassPoint
            {
                get
                {
                    var rankClass = RankClass;
                    var cnt = 1;
                    while (cnt + 1 <= 5)
                    {
                        if (cnt == _rankClassList.Count) break;
                        if (_rankClassList[^(cnt + 1)] != rankClass) break;
                        ++ cnt;
                    }

                    if (cnt == 1)
                    {
                        // 降格者は +1 それ以外は -1
                        return (_rankClassList.Count >= 2 && _rankClassList[^2] < RankClass ? +1 : -1, 0);
                    }

                    var sum = 0;
                    var classPoint = double.MinValue;
                    for (var i = 1; i <= cnt; ++i)
                    {
                        sum += _rankPointList[^i];
                        if (i == 1) continue;
                        classPoint = Math.Max(classPoint, sum / Math.Sqrt(i));
                    }

                    return (0, classPoint);
                }
            }

            public void SetRank(int rankClass, int rank)
            {
                _rankList.Add(rank);
                _rankClassList.Add(rankClass);
                _rankPointList.Add(0);
            }

            public void AddRankPoint(int val, bool isFinal)
            {
                if (isFinal)
                {
                    FinalRankPoint += val;
                }
                else
                {
                    _rankPointList[^1] += val;
                }
            }
        }
    }
}
