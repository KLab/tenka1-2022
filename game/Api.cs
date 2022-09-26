using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using StackExchange.Redis;

namespace game
{
    internal class Api
    {
        public const string AdminUserId = "cvrb3f80we";
        private const string AdminToken = "rzz46qxw4vd";

        private const string HealthEndpoint = "/health";
        private const string JoinEndpoint = "/api/join/";
        private const string StartEndpoint = "/api/start/";
        private const string MoveEndpoint = "/api/move/";
        private const string DataEndpoint = "/api/data/";
        private const string EventEndpoint = "/event/";

        private static readonly byte[] SseHeader = Encoding.UTF8.GetBytes("data: ");
        private static readonly byte[] SseFooter = Encoding.UTF8.GetBytes("\n\n");
        private static readonly byte[] SsePing = Encoding.UTF8.GetBytes("event: ping\ndata: \n\n");

        private static readonly Regex ReNum = new(@"^([0-9]|[1-9][0-9]{1,8})$", RegexOptions.Compiled);
        private static readonly Regex ReToken = new(@"^[0-9a-z]+$", RegexOptions.Compiled);

        private static readonly Random RandomObj = new();
        private static readonly ConcurrentDictionary<string, (string, long)> UserIdCache = new();

        private readonly ConnectionMultiplexer _redis;
        private readonly long _startAt;
        private readonly long _endAt;
        private readonly LoadedLuaScript _runGameScript;
        private readonly LoadedLuaScript _runJoinScript;
        private readonly LoadedLuaScript _runStartScript;
        private readonly LoadedLuaScript _runMoveScript1;
        private readonly LoadedLuaScript _runMoveScript2;
        private readonly LoadedLuaScript _runDataScript;
        private readonly LoadedLuaScript _runGetGameIdScript;
        private readonly LoadedLuaScript _runRankingScript;
        private readonly LoadedLuaScript _runTimeLimitScript;
        private readonly HttpListener _listener;

        private const int SseCancelTime = 5000;
        private const int SseTimeLimit = 1000;
        private const int JoinTimeLimit = 1000;
        private const int DataTimeLimit = 1000;

        public Api(ConnectionMultiplexer redis, ConfigurationOptions redisConfig, string port, long startAt, long endAt)
        {
            _redis = redis;
            _startAt = startAt;
            _endAt = endAt;

            var gamePrepared = LuaScript.Prepare(@"
local user_id = @userId
local now = tonumber(@now)

local j_game_id_list = redis.call('lrange', 'j_game_id_list_'..user_id, 0, -1)
local j_game_ids = {}
for i = 1, #j_game_id_list, 2 do
  table.insert(j_game_ids, tonumber(j_game_id_list[i]))
end

local s_game_id_list = redis.call('lrange', 's_game_id_list_'..user_id, 0, -1)
local s_game_ids = {}
for i = 1, #s_game_id_list do
  table.insert(s_game_ids, tonumber(s_game_id_list[i]))
end

local running = {}
for i = #j_game_id_list, 1, -2 do
  local game_id = j_game_id_list[i-1]
  local finish_time = tonumber(j_game_id_list[i])
  if finish_time < now then break end
  table.insert(running, 1, game_id)
end

local ranking_time = -1
local rank = -1
if #running > 0 then
  ranking_time = redis.call('lindex', 'ranking_times', -1)
  rank = redis.call('zscore', 'ranking_'..tostring(ranking_time), user_id) or -1
end

if #s_game_id_list >= 1 then
  local last_game_id = s_game_id_list[#s_game_id_list]
  local last_start_time = tonumber(redis.call('hget', 'start_time', last_game_id))
  if now < last_start_time + "+(GameLogic.TotalTurn * GameLogic.TurnPeriod)+@" then
    table.insert(running, last_game_id)
  end
end

return {j_game_ids, s_game_ids, running, ranking_time, rank}
");
            var joinPrepared = LuaScript.Prepare(@"
local user_id = @userId
local now = tonumber(@now)
local join_t2 = tonumber(redis.call('get', 'join_t2'))
if now < join_t2 then
  redis.call('hsetnx', 'join_pool_1', user_id, redis.call('hlen', 'join_pool_1'))
else
  redis.call('hsetnx', 'join_pool_2', user_id, redis.call('hlen', 'join_pool_2'))
end
local llen = redis.call('llen', 'j_game_id_list_'..user_id)
local game_ids = {}
for i = llen-1, 0, -2 do
  local game_id = redis.call('lindex', 'j_game_id_list_'..user_id, i-1)
  local finish_time = tonumber(redis.call('lindex', 'j_game_id_list_'..user_id, i))
  if finish_time < now then break end
  table.insert(game_ids, tonumber(game_id))
end
return {'ok', game_ids}
");
            var startPrepared = LuaScript.Prepare(@"
local user_id = @userId
local now = tonumber(@now)
local mode = tonumber(@mode)
local start_time = tonumber(@startTime)
local last_game_id = redis.call('lindex', 's_game_id_list_'..user_id, -1)
if last_game_id then
  last_game_id = tonumber(last_game_id)
  local last_start_time = tonumber(redis.call('hget', 'start_time', last_game_id))
  if now < last_start_time + "+(GameLogic.TotalTurn * GameLogic.TurnPeriod)+@" then
    return {'started', last_game_id}
  end
end
local game_id = redis.call('incr', 'game_id_counter')
redis.call('rpush', 'observed_game_ids', game_id)
redis.call('hset', 'start_time', game_id, start_time)
if mode == 0 then
  redis.call('hset', 'game_member', game_id, user_id)
else
  redis.call('hset', 'game_member', game_id, user_id..'/#/#/#/#/#')
end
redis.call('rpush', 's_game_id_list_'..user_id, game_id)
redis.call('publish', user_id, 'SS'..tostring(game_id))
return {'ok', game_id}
");
            var movePrepared1 = LuaScript.Prepare(@"
local user_id = @userId
local game_id = @gameId
local dir = tonumber(@dir)
local now = tonumber(@now)
local game_member = {}
local member_id = nil
for w in string.gmatch(redis.call('hget', 'game_member', game_id), '[^/]+') do
  if w == user_id then
    member_id = #game_member
  end
  table.insert(game_member, w)
end
if not member_id then
  return {'invalid_game_id'}
end
local start_time = tonumber(redis.call('hget', 'start_time', game_id))
local turn = math.floor((now - start_time) / "+GameLogic.TurnPeriod+@")
local turn_fixed = tonumber(redis.call('hget', 'turn_fixed', game_id) or -1)
if turn < turn_fixed + 1 then
  turn = turn_fixed + 1
end
if turn >= "+GameLogic.TotalTurn+@" then
  return {'game_finished'}
end
if redis.call('hsetnx', 'move_hash', game_id..'/'..tostring(turn)..'/'..tostring(member_id), dir) ~= 1 then
  return {'already_moved'}
end
if redis.call('sismember', 'running_game_ids', game_id) == 1 then
  redis.call('sadd', 'lottery_users', user_id)
end
return {'ok', member_id, start_time + "+GameLogic.TurnPeriod+@" * (turn + 1)}
");
            var movePrepared2 = LuaScript.Prepare(@"
local game_id = @gameId
local now = tonumber(@now)
local rnd = @rnd
local start_time = tonumber(redis.call('hget', 'start_time', game_id))
local turn_target = math.floor((now - start_time) / "+GameLogic.TurnPeriod+@") - 1
if turn_target >= "+(GameLogic.TotalTurn-1)+@" then
  turn_target = "+(GameLogic.TotalTurn-1)+@"
end
local turn = tonumber(redis.call('hget', 'turn_fixed', game_id) or -1)
local game_member = {}
for w in string.gmatch(redis.call('hget', 'game_member', game_id), '[^/]+') do
  table.insert(game_member, w)
end
if turn < turn_target then
  turn = turn + 1
  local dir = {}
  local s = ''
  for i = 0, 5 do
    local d = -1
    if game_member[i+1] then
      if game_member[i+1] == '#' then
        d = rnd % 4
        rnd = (rnd - d) / 4
      else
        d = redis.call('hget', 'move_hash', game_id..'/'..tostring(turn)..'/'..tostring(i)) or -1
      end
    end
    table.insert(dir, d)
    s = s .. '/' .. tostring(d)
  end
  redis.call('rpush', 'move_list_'..game_id, dir[1], dir[2], dir[3], dir[4], dir[5], dir[6])
  redis.call('hset', 'turn_fixed', game_id, turn)
  for i, w in ipairs(game_member) do
    redis.call('publish', w, 'M'..tostring(game_id)..'/'..tostring(turn)..'/'..tostring(i-1)..s)
  end
end
if turn < turn_target then
  return {'continue'}
end
local move_list = redis.call('lrange', 'move_list_'..game_id, 0, -1)
local need_game_score = (turn == "+(GameLogic.TotalTurn-1)+@") and (redis.call('hexists', 'game_score', game_id) ~= 1)
return {'ok', move_list, need_game_score}
");
            var dataPrepared = LuaScript.Prepare(@"
local user_id = @userId
local game_id = @gameId
local game_member = {}
local member_id = nil
for w in string.gmatch(redis.call('hget', 'game_member', game_id), '[^/]+') do
  if w == user_id then
    member_id = #game_member
  end
  table.insert(game_member, w)
end
if user_id == '"+AdminUserId+@"' then
  member_id = 0
end
if not member_id then
  return {'invalid_game_id'}
end
local move_list = redis.call('lrange', 'move_list_'..game_id, 0, -1)
return {'ok', member_id, move_list, game_member}
");
            var getGameIdPrepared = LuaScript.Prepare(@"
local now = tonumber(@now)
local llen = redis.call('llen', 'observed_game_ids')
for i = 1, llen do
  local game_id = redis.call('lpop', 'observed_game_ids')
  local start_time = tonumber(redis.call('hget', 'start_time', game_id))
  local turn_target = math.floor((now - start_time) / "+GameLogic.TurnPeriod+@") - 1
  local turn_fixed = tonumber(redis.call('hget', 'turn_fixed', game_id) or -1)
  if turn_fixed < "+(GameLogic.TotalTurn-1)+@" then
    redis.call('rpush', 'observed_game_ids', game_id)
  end
  if turn_fixed < turn_target then
    return game_id
  end
end
return -1
");
            var rankingPrepared = LuaScript.Prepare(@"
local ranking_time = @rankingTime
local rank0 = @i
local rank1 = @j
local user_ids = redis.call('zrange', 'ranking_'..tostring(ranking_time), rank0, rank1 - 1)
local res = {}
local cp_key1 = 'rank_class_'..tostring(ranking_time)
local cp_key2 = 'class_point_'..tostring(ranking_time)
for _, user_id in ipairs(user_ids) do
  table.insert(res, user_id)
  table.insert(res, redis.call('hget', cp_key1, user_id) or -1)
  table.insert(res, redis.call('hget', cp_key2, user_id) or '')
end
return res
");
            var timeLimitPrepared = LuaScript.Prepare(@"
local field = @field
local now = tonumber(@now)
local time_limit = tonumber(@timeLimit)
local t = redis.call('hget', 'unlock_time', field)
if t and now < tonumber(t) then
  return tostring(t)
end
redis.call('hset', 'unlock_time', field, now + time_limit)
return 'ok'
");
            Debug.Assert(redisConfig.EndPoints.Count == 1);
            var server = redis.GetServer(redisConfig.EndPoints[0]);
            _runGameScript = gamePrepared.Load(server);
            _runJoinScript = joinPrepared.Load(server);
            _runStartScript = startPrepared.Load(server);
            _runMoveScript1 = movePrepared1.Load(server);
            _runMoveScript2 = movePrepared2.Load(server);
            _runDataScript = dataPrepared.Load(server);
            _runGetGameIdScript = getGameIdPrepared.Load(server);
            _runRankingScript = rankingPrepared.Load(server);
            _runTimeLimitScript = timeLimitPrepared.Load(server);

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");
        }

        public void Start(CancellationToken cts)
        {
            new Thread(() => Observe(cts)).Start();
            _listener.Start();
            _listener.BeginGetContext(OnRequest, _listener);
            cts.Register(() => _listener.Close());
        }

        private void Observe(CancellationToken cts)
        {
            var db = _redis.GetDatabase();

            while (!cts.IsCancellationRequested)
            {
                var gameId = (int)_runGetGameIdScript.Evaluate(db, new { now = GetTime() - 10 });
                if (gameId < 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                var res = (RedisResult[])_runMoveScript2.Evaluate(db, new { gameId, now = GetTime(), rnd = GetRnd() });
                if ((string)res[0] != "ok") continue;
                if (!(bool)res[2]) continue;

                var gameLogic = new GameLogic(0, (int[])res[1]);
                var score = gameLogic.GetScoreToSave(0);
                db.HashSet("game_score", gameId, string.Join(' ', score.Select(x => $"{x}")));
            }
        }

        private void OnRequest(IAsyncResult result)
        {
            if (!_listener.IsListening) return;

            var context = _listener.EndGetContext(result);
            _listener.BeginGetContext(OnRequest, _listener);

            new Thread(() => OnRequest(context)).Start();
        }

        private void OnRequest(HttpListenerContext context)
        {
            var rawUrl = context.Request.RawUrl;
            Debug.Assert(rawUrl != null, nameof(rawUrl) + " != null");
            Console.WriteLine("rawUrl = " + rawUrl);
            context.Response.AppendHeader("Access-Control-Allow-Origin", "*");
            if (rawUrl.StartsWith(EventEndpoint, StringComparison.Ordinal))
            {
                try
                {
                    RunSse(context.Response, rawUrl);
                }
                catch (HttpListenerException)
                {
                    context.Response.Abort();
                }
                catch (Exception e2)
                {
                    Console.Error.WriteLine(e2);
                    context.Response.Abort();
                }
            }
            else if (rawUrl.StartsWith(JoinEndpoint, StringComparison.Ordinal))
            {
                OnRequestMain(context, rawUrl, RunJoin);
            }
            else if (rawUrl.StartsWith(StartEndpoint, StringComparison.Ordinal))
            {
                OnRequestMain(context, rawUrl, RunStart);
            }
            else if (rawUrl.StartsWith(MoveEndpoint, StringComparison.Ordinal))
            {
                OnRequestMain(context, rawUrl, RunMove);
            }
            else if (rawUrl.StartsWith(DataEndpoint, StringComparison.Ordinal))
            {
                OnRequestMain(context, rawUrl, RunData);
            }
            else if (rawUrl.StartsWith(HealthEndpoint, StringComparison.Ordinal))
            {
                var response = context.Response;
                response.StatusCode = (int)HttpStatusCode.OK;
                response.Close();
            }
            else
            {
                var response = context.Response;
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
            }
        }

        private static void OnRequestMain(HttpListenerContext context, string rawUrl, Func<string, byte[]?> func)
        {
            byte[]? responseString;
            try
            {
                responseString = func(rawUrl);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.ContentType = "text/plain";
                try
                {
                    context.Response.Close();
                }
                catch (HttpListenerException)
                {
                    context.Response.Abort();
                }
                catch (Exception e2)
                {
                    Console.Error.WriteLine(e2);
                    context.Response.Abort();
                }

                return;
            }

            if (responseString == null)
            {
                var response = context.Response;
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            try
            {
                var response = context.Response;
                response.StatusCode = (int)HttpStatusCode.OK;
                response.OutputStream.Write(responseString);
                response.Close();
            }
            catch (HttpListenerException)
            {
                context.Response.Abort();
            }
            catch (Exception e2)
            {
                Console.Error.WriteLine(e2);
                context.Response.Abort();
            }
        }

        private static void WaitUntil(long t)
        {
            for (;;)
            {
                var now = GetTime();
                if (t <= now) return;
                Thread.Sleep((int)(t - now));
            }
        }

        private long? WaitUnlock(string type, string userId, int timeLimit)
        {
            var now = GetTime();
            var unlockTime = -1L;
            for (;;)
            {
                var ut = GetSetTimeLimit(type, userId, now, timeLimit);
                if (ut < 0)
                {
                    break;
                }

                if (unlockTime < 0)
                {
                    unlockTime = ut;
                }
                else if (unlockTime != ut)
                {
                    return null;
                }

                Thread.Sleep((int)Math.Max(1, unlockTime - now));
                now = GetTime();
                while (now < unlockTime)
                {
                    Thread.Sleep(1);
                    now = GetTime();
                }
            }

            return now;
        }

        private byte[]? RunJoin(string rawUrl)
        {
            // /api/join/([0-9a-z]+)
            Debug.Assert(rawUrl.StartsWith(JoinEndpoint, StringComparison.Ordinal));
            var token = rawUrl[JoinEndpoint.Length..];

            if (!ReToken.IsMatch(token)) return null;

            var userId = GetUserId(token);
            if (userId == null) return null;

            var nowNullable = WaitUnlock("join", userId, JoinTimeLimit);
            if (!nowNullable.HasValue)
            {
                return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                {
                    ["status"] = "error_time_limit",
                });
            }

            var db = _redis.GetDatabase();

            var now = GetTime();
            if (now < _startAt) return null;

            var res = (RedisResult[])_runJoinScript.Evaluate(db, new { userId, now });
            return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["status"] = (string)res[0],
                ["game_ids"] = ((int[])res[1]).Reverse(),
            });
        }

        private byte[]? RunStart(string rawUrl)
        {
            // /api/start/([0-9a-z]+)/([0-9]+)/([0-9]+)
            Debug.Assert(rawUrl.StartsWith(StartEndpoint, StringComparison.Ordinal));
            var paramStr = rawUrl[StartEndpoint.Length..];

            var param = paramStr.Split('/');
            if (param.Length != 3) return null;

            if (!(ReToken.IsMatch(param[0]) && ReNum.IsMatch(param[1]) && ReNum.IsMatch(param[2]))) return null;

            var token = param[0];
            var mode = int.Parse(param[1]);
            var delay = int.Parse(param[2]);

            if (!(mode == 0 || mode == 1)) return null;
            if (delay > 10) return null;

            var userId = GetUserId(token);
            if (userId == null) return null;

            var db = _redis.GetDatabase();

            var now = GetTime();
            if (now < _startAt) return null;
            if (now >= _endAt)
            {
                return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                {
                    ["status"] = "game_finished",
                });
            }

            var startTime = now + 1000 * delay;
            var res = (RedisResult[])_runStartScript.Evaluate(db, new { userId, now, mode, startTime });
            return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["status"] = (string)res[0],
                ["start"] = startTime,
                ["game_id"] = (int)res[1],
            });
        }

        private byte[]? RunMove(string rawUrl)
        {
            // /api/move/([0-9a-z]+)/([0-9]+)/([0-9]+)
            Debug.Assert(rawUrl.StartsWith(MoveEndpoint, StringComparison.Ordinal));
            var paramStr = rawUrl[MoveEndpoint.Length..];

            var param = paramStr.Split('/');
            if (param.Length != 3) return null;

            if (!(ReToken.IsMatch(param[0]) && ReNum.IsMatch(param[1]) && ReNum.IsMatch(param[2]))) return null;

            var token = param[0];
            var gameId = int.Parse(param[1]);
            var dir = int.Parse(param[2]);
            // ReSharper disable once MergeIntoPattern
            if (!(0 <= dir && dir <= 3)) return null;

            var userId = GetUserId(token);
            if (userId == null) return null;

            var db = _redis.GetDatabase();
            int memberId;
            {
                var now = GetTime();
                var res = (RedisResult[])_runMoveScript1.Evaluate(db, new { userId, gameId, dir, now });
                if ((string)res[0] != "ok")
                {
                    return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                    {
                        ["status"] = (string)res[0],
                    });
                }

                memberId = (int)res[1];
                WaitUntil((long)res[2]);
            }

            {
                var now = GetTime();
                for (;;)
                {
                    var res = (RedisResult[])_runMoveScript2.Evaluate(db, new { gameId, now, rnd = GetRnd() });
                    if ((string)res[0] == "continue") continue;
                    if ((string)res[0] != "ok")
                    {
                        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                        {
                            ["status"] = (string)res[0],
                        });
                    }

                    var gameLogic = new GameLogic(memberId, (int[])res[1]);
                    if ((bool)res[2])
                    {
                        var score = gameLogic.GetScoreToSave(memberId);
                        db.HashSet("game_score", gameId, string.Join(' ', score.Select(x => $"{x}")));
                    }

                    return JsonSerializer.SerializeToUtf8Bytes(gameLogic.GetResponseData(GetTime()));
                }
            }
        }

        private byte[]? RunData(string rawUrl)
        {
            // /api/data/([0-9a-z]+)/([0-9]+)
            Debug.Assert(rawUrl.StartsWith(DataEndpoint, StringComparison.Ordinal));
            var paramStr = rawUrl[DataEndpoint.Length..];

            var param = paramStr.Split('/');
            if (param.Length != 2) return null;

            if (!(ReToken.IsMatch(param[0]) && ReNum.IsMatch(param[1]))) return null;

            var token = param[0];
            var gameId = int.Parse(param[1]);

            var userId = token == AdminToken ? AdminUserId : GetUserId(token);
            if (userId == null) return null;

            var nowNullable = WaitUnlock("data", userId, DataTimeLimit);
            if (!nowNullable.HasValue)
            {
                return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                {
                    ["status"] = "error_time_limit",
                });
            }

            var db = _redis.GetDatabase();

            var res = (RedisResult[])_runDataScript.Evaluate(db, new { userId, gameId });
            var status = (string)res[0];
            if (status != "ok")
            {
                return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                {
                    ["status"] = status,
                });
            }

            var memberId = (int)res[1];
            var moveList = (int[])res[2];
            var moves = new List<int[]>();
            for (var i = 0; i < moveList.Length; i += 6)
            {
                moves.Add(GameLogic.ConvertMove(memberId, moveList[i..(i+6)]));
            }
            return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["status"] = "ok",
                ["moves"] = moves,
                ["user_ids"] = moves.Count >= GameLogic.TotalTurn ? GameLogic.ConvertUserIds(memberId, (string[])res[3]) : Array.Empty<string>(),
            });
        }

        private void RunSse(HttpListenerResponse response, string rawUrl)
        {
            // /event/([0-9a-z]+)
            Debug.Assert(rawUrl.StartsWith(EventEndpoint, StringComparison.Ordinal));
            var token = rawUrl[EventEndpoint.Length..];
            if (token == AdminToken)
            {
                RunAdminSse(response);
                return;
            }

            var connectTime = GetTime();

            var userId = GetUserId(token);
            if (userId == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }

            response.AppendHeader("Content-Type", "text/event-stream");
            response.AppendHeader("X-Accel-Buffering", "no"); // to avoid buffering in nginx

            if (GetSetTimeLimit("SSE", userId, connectTime, SseTimeLimit) >= 0)
            {
                response.OutputStream.Write(SseHeader);
                response.OutputStream.Write(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                {
                    ["type"] = "error_time_limit",
                }));
                response.OutputStream.Write(SseFooter);
                response.OutputStream.Flush();
                return;
            }

            var db = _redis.GetDatabase();
            db.Publish(userId, $"C{connectTime}");

            Console.WriteLine("start SSE");

            var ch = Channel.CreateUnbounded<byte[]>();
            var sub = _redis.GetSubscriber().Subscribe(userId);
            try
            {
                sub.OnMessage(message => ch.Writer.WriteAsync(message.Message).AsTask().Wait());

                Thread.Sleep(500);
                var subStartTime = GetTime();

                {
                    response.OutputStream.Write(SseHeader);
                    var (s, t, r) = GetGameSse(db, userId, subStartTime);
                    response.OutputStream.Write(s);
                    response.OutputStream.Write(SseFooter);
                    response.OutputStream.Flush();
                    if (r != -1)
                    {
                        response.OutputStream.Write(SseHeader);
                        response.OutputStream.Write(GetRankingSse(db, t, r));
                        response.OutputStream.Write(SseFooter);
                        response.OutputStream.Flush();
                    }
                }

                for (;;)
                {
                    var cancel = new CancellationTokenSource();
                    cancel.CancelAfter(SseCancelTime);
                    try
                    {
                        var task = ch.Reader.ReadAsync(cancel.Token).AsTask();
                        task.Wait(cancel.Token);
                        var msg = task.Result;
                        switch (msg[0])
                        {
                            case (byte)'S':
                            {
                                response.OutputStream.Write(SseHeader);
                                response.OutputStream.Write(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                                {
                                    ["type"] = "start",
                                    ["game_id"] = int.Parse(Encoding.UTF8.GetString(msg.AsSpan()[2..])),
                                    ["join"] = msg[1] == (byte)'J',
                                }));
                                response.OutputStream.Write(SseFooter);
                                response.OutputStream.Flush();
                                break;
                            }
                            case (byte)'M':
                            {
                                var a = Encoding.UTF8.GetString(msg.AsSpan()[1..]).Split('/').Select(int.Parse).ToArray();
                                Debug.Assert(a.Length == 9);
                                response.OutputStream.Write(SseHeader);
                                var gameId = a[0];
                                response.OutputStream.Write(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                                {
                                    ["type"] = "move",
                                    ["game_id"] = gameId,
                                    ["turn"] = a[1],
                                    ["move"] = GameLogic.ConvertMove(a[2], a[3..]),
                                }));
                                response.OutputStream.Write(SseFooter);
                                response.OutputStream.Flush();
                                break;
                            }
                            case (byte)'R':
                            {
                                var a = Encoding.UTF8.GetString(msg.AsSpan()[1..]).Split('/');
                                var rankingTime = long.Parse(a[0]);
                                var rank = int.Parse(a[1]);
                                response.OutputStream.Write(SseHeader);
                                response.OutputStream.Write(GetRankingSse(db, rankingTime, rank));
                                response.OutputStream.Write(SseFooter);
                                response.OutputStream.Flush();
                                break;
                            }
                            case (byte)'C':
                            {
                                if (long.Parse(Encoding.UTF8.GetString(msg.AsSpan()[1..])) != connectTime)
                                {
                                    response.OutputStream.Write(SseHeader);
                                    response.OutputStream.Write(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                                    {
                                        ["type"] = "disconnected",
                                    }));
                                    response.OutputStream.Write(SseFooter);
                                    response.OutputStream.Flush();
                                    return;
                                }
                                break;
                            }
                            default:
                                throw new Exception(Encoding.UTF8.GetString(msg));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        response.OutputStream.Write(SsePing);
                        response.OutputStream.Flush();
                    }
                }
            }
            catch (Exception)
            {
                sub.Unsubscribe();
                throw;
            }
        }

        private void RunAdminSse(HttpListenerResponse response)
        {
            response.AppendHeader("Content-Type", "text/event-stream");
            response.AppendHeader("X-Accel-Buffering", "no"); // to avoid buffering in nginx

            var db = _redis.GetDatabase();

            var ch = Channel.CreateUnbounded<byte[]>();
            var sub = _redis.GetSubscriber().Subscribe(AdminUserId);
            try
            {
                sub.OnMessage(message => ch.Writer.WriteAsync(message.Message).AsTask().Wait());

                Thread.Sleep(500);
                var subStartTime = GetTime();

                {
                    response.OutputStream.Write(SseHeader);
                    var (s, t, _) = GetGameSse(db, AdminUserId, subStartTime);
                    response.OutputStream.Write(s);
                    response.OutputStream.Write(SseFooter);
                    response.OutputStream.Flush();

                    var rt2 = db.ListGetByIndex("ranking_times", -2);
                    if (!rt2.IsNull)
                    {
                        var gameIdVal = db.ListGetByIndex("game_ids_" + (string)rt2, 0);
                        if (!gameIdVal.IsNull)
                        {
                            response.OutputStream.Write(SseHeader);
                            response.OutputStream.Write(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                            {
                                ["type"] = "play",
                                ["game_id"] = (int)gameIdVal,
                            }));
                            response.OutputStream.Write(SseFooter);
                            response.OutputStream.Flush();
                        }
                    }
                }

                for (;;)
                {
                    var cancel = new CancellationTokenSource();
                    cancel.CancelAfter(SseCancelTime);
                    try
                    {
                        var task = ch.Reader.ReadAsync(cancel.Token).AsTask();
                        task.Wait(cancel.Token);
                        var msg = task.Result;
                        switch (msg[0])
                        {
                            case (byte)'R':
                            {
                                var a = Encoding.UTF8.GetString(msg.AsSpan()[1..]).Split('/');
                                var rankingTime = long.Parse(a[0]);
                                var rank = int.Parse(a[1]);
                                response.OutputStream.Write(SseHeader);
                                response.OutputStream.Write(GetRankingSse(db, rankingTime, rank));
                                response.OutputStream.Write(SseFooter);
                                response.OutputStream.Flush();

                                var rt2 = db.ListGetByIndex("ranking_times", -2);
                                if (!rt2.IsNull)
                                {
                                    var gameIdVal = db.ListGetByIndex("game_ids_" + (string)rt2, 0);
                                    if (!gameIdVal.IsNull)
                                    {
                                        response.OutputStream.Write(SseHeader);
                                        response.OutputStream.Write(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                                        {
                                            ["type"] = "play",
                                            ["game_id"] = (int)gameIdVal,
                                        }));
                                        response.OutputStream.Write(SseFooter);
                                        response.OutputStream.Flush();
                                    }
                                }

                                break;
                            }
                            case (byte)'F':
                            {
                                var a = Encoding.UTF8.GetString(msg.AsSpan()[1..]).Split('!');
                                var rankingTime = long.Parse(a[0]);
                                var gameId = int.Parse(a[1]);
                                var users = new List<Dictionary<string, object>>();
                                for (var k = 2; k + 1 < a.Length; k += 2)
                                {
                                    users.Add(new Dictionary<string, object>
                                    {
                                        ["user_id"] = a[k],
                                        ["score"] = a[k+1],
                                    });
                                }

                                response.OutputStream.Write(SseHeader);
                                response.OutputStream.Write(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                                {
                                    ["type"] = "ranking",
                                    ["class"] = -1,
                                    ["users"] = users,
                                    ["time"] = rankingTime,
                                }));
                                response.OutputStream.Write(SseFooter);
                                response.OutputStream.Flush();

                                if (gameId >= 0)
                                {
                                    response.OutputStream.Write(SseHeader);
                                    response.OutputStream.Write(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                                    {
                                        ["type"] = "play",
                                        ["game_id"] = gameId,
                                    }));
                                    response.OutputStream.Write(SseFooter);
                                    response.OutputStream.Flush();
                                }

                                break;
                            }
                            default:
                                throw new Exception(Encoding.UTF8.GetString(msg));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        response.OutputStream.Write(SsePing);
                        response.OutputStream.Flush();
                    }
                }
            }
            catch (Exception)
            {
                sub.Unsubscribe();
                throw;
            }
        }

        private (byte[], long, int) GetGameSse(IDatabase db, string userId, long now)
        {
            var res = (RedisResult[])_runGameScript.Evaluate(db, new { userId, now });

            return (JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["type"] = "game",
                ["now"] = now,
                ["user_id"] = userId,
                ["total_turn"] = GameLogic.TotalTurn,
                ["j_game_ids"] = (int[])res[0],
                ["s_game_ids"] = (int[])res[1],
                ["running"] = (int[])res[2],
            }), (long)res[3], (int)res[4]);
        }

        private byte[] GetRankingSse(IDatabase db, long rankingTime, int rank)
        {
            if (rank < 0)
            {
                return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                {
                    ["type"] = "ranking",
                    ["class"] = 0,
                    ["users"] = new List<object>(),
                    ["time"] = rankingTime,
                });
            }

            var (c, i, j) = GetClassData(rank);
            var res = (RedisResult[])_runRankingScript.Evaluate(db, new { rankingTime, i, j });
            var users = new List<Dictionary<string, object>>();
            for (var k = 0; k + 2 < res.Length; k += 3)
            {
                users.Add(new Dictionary<string, object>
                {
                    ["user_id"] = (string)res[k],
                    ["score"] = c == (int)res[k+1] ? (string)res[k+2] : "N/A",
                });
            }

            return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["type"] = "ranking",
                ["class"] = c,
                ["users"] = users,
                ["time"] = rankingTime,
            });
        }

        private static (int, int, int) GetClassData(int rank)
        {
            var i = 0;
            for (var c = 1; ; ++c)
            {
                var j = CalcScore.ClassFunc(c);
                if (rank < j) return (c, i, j);
                i = j;
            }
        }

        private long GetSetTimeLimit(string type, string userId, long now, int timeLimit)
        {
            var db = _redis.GetDatabase();
            var field = $"{type}_{userId}";
            var r = (string)_runTimeLimitScript.Evaluate(db, new { field, now, timeLimit });
            return r == "ok" ? -1 : long.Parse(r);
        }

        private string? GetUserId(string token)
        {
            var now = GetTime();
            if (UserIdCache.TryGetValue(token, out var cache))
            {
                if (now < cache.Item2)
                {
                    return cache.Item1;
                }
            }

            var db = _redis.GetDatabase();
            var res = db.HashGet("user_token", token);
            if (res.IsNull)
            {
                return null;
            }
            else
            {
                var s = res.ToString();
                UserIdCache[token] = (s, now + 10000);
                return s;
            }
        }

        private static int GetRnd()
        {
            int res;
            lock (RandomObj)
            {
                res = RandomObj.Next() & 4095;  // 4095 = 4**6 - 1
            }

            return res;
        }

        public static long GetTime()
        {
            return (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10000;
        }
    }
}
