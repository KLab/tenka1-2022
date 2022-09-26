using System.Collections.Generic;
using System.Diagnostics;

namespace game;

public class GameLogic
{
    public const int TotalTurn = 294;
    public const int TurnPeriod = 500;

    private const int N = 5;
    private static readonly int[] Dj = { +1, 0, -1, 0 };
    private static readonly int[] Dk = { 0, +1, 0, -1 };

    private readonly Cell[] _field;
    private readonly Agent[] _agents;
    private readonly int _turn;
    private readonly int[] _move;
    private readonly int[] _score;
    private readonly int[] _area;

    public static int[] ConvertMove(int memberId, int[] a)
    {
        var res = new int[6];
        for (var idx = 0; idx < 6; idx++)
        {
            res[idx] = a[Func1(memberId, idx)];
        }

        return res;
    }

    public static string[] ConvertUserIds(int memberId, string[] a)
    {
        if (a.Length < 6) return a;
        var res = new string[6];
        for (var idx = 0; idx < 6; idx++)
        {
            res[idx] = a[Func1(memberId, idx)];
        }

        return res;
    }

    public int[] GetScoreToSave(int memberId)
    {
        var res = new int[6];
        for (var idx = 0; idx < 6; idx++)
        {
            res[Func1(memberId, idx)] = _score[idx];
        }

        return res;
    }

    public Dictionary<string, object> GetResponseData(long now)
    {
        var field = new int[6][][][];
        var agent = new int[6][];
        for (var i = 0; i < 6; i++)
        {
            agent[i] = new[]{ _agents[i].I, _agents[i].J, _agents[i].K, _agents[i].D };
            field[i] = new int[N][][];
            for (var j = 0; j < N; j++)
            {
                field[i][j] = new int[N][];
                for (var k = 0; k < N; k++)
                {
                    var c = _field[FieldIdx(i,j,k)];
                    field[i][j][k] = new[]{ c.Owner, c.Val };
                }
            }
        }

        return new Dictionary<string, object>
        {
            ["status"] = "ok",
            ["now"] = now,
            ["turn"] = _turn,
            ["move"] = _move,
            ["score"] = _score,
            ["field"] = field,
            ["agent"] = agent,
        };
    }

    public GameLogic(int memberId, IReadOnlyList<int> moveList)
    {
        Debug.Assert(moveList.Count % 6 == 0);

        _field = new Cell[6*N*N];
        _agents = new Agent[6];
        _turn = moveList.Count / 6;
        _move = new int[6];
        _score = new int[6];
        _area = new int[6];
        for (var i = 0; i < _field.Length; i++)
        {
            _field[i] = new Cell { Owner = -1, Val = 0 };
        }
        for (var i = 0; i < 6; i++)
        {
            _agents[i] = new Agent { I = i, J = N / 2, K = N / 2, D = 0 };
            var fi = FieldIdx(i, N / 2, N / 2);
            _field[fi].Owner = i;
            _field[fi].Val = 2;
            _area[i] = 1;
        }

        var counter = new byte[6*N*N];
        var fis = new int[6];
        for (int i = 0, turn = 0; i < moveList.Count; i += 6, turn++)
        {
            for (var idx = 0; idx < 6; idx++)
            {
                _move[idx] = moveList[i + Func1(memberId, idx)];
                if (_move[idx] == -1) continue;
                RotateAgent(idx, _move[idx]);
                MoveForward(idx);
                var ii = _agents[idx].I;
                var jj = _agents[idx].J;
                var kk = _agents[idx].K;
                fis[idx] = FieldIdx(ii, jj, kk);
                ++counter[fis[idx]];
            }

            for (var idx = 0; idx < 6; idx++)
            {
                if (_move[idx] == -1) continue;
                if (counter[fis[idx]] == 1 || _field[fis[idx]].Owner == idx)
                {
                    Paint(idx, fis[idx]);
                }
            }

            for (var idx = 0; idx < 6; idx++)
            {
                if (_move[idx] == -1) continue;
                --counter[fis[idx]];
            }

            if (turn >= TotalTurn / 2)
            {
                AddScore();
            }
        }
    }

    private void AddScore()
    {
        for (var i = 0; i < 6; i++)
        {
            _score[i] += _area[i];
        }
    }

    private static int Func1(int memberId, int pos)
    {
        var i0 = memberId / 3;
        var i1 = memberId % 3;
        var j0 = pos / 3;
        var j1 = pos % 3;
        return ((j0 + 1) * i1 + j1) % 3 + (i0 + j0) % 2 * 3;
        // 012345
        // 120534
        // 201453
        // 345012
        // 453201
        // 534120
        /*
        if (i0 == 0)
        {
            // return j0 == 0 ? (i1 + j1) % 3 : (2 * i1 + j1) % 3 + 3;
            return ((j0 + 1) * i1 + j1) % 3 + 3 * j0;
        }
        else
        {
            // return j0 == 0 ? (i1 + j1) % 3 + 3 : (2 * i1 + j1) % 3;
            return ((j0 + 1) * i1 + j1) % 3 + 3 * (1 - j0);
        }
        */
    }

    private void Paint(int idx, int fi)
    {
        if (_field[fi].Owner == -1)
        {
            ++ _area[idx];
            _field[fi].Owner = idx;
            _field[fi].Val = 2;
        }
        else if (_field[fi].Owner == idx)
        {
            _field[fi].Val = 2;
        }
        else if (_field[fi].Val == 1)
        {
            -- _area[_field[fi].Owner];
            _field[fi].Owner = -1;
            _field[fi].Val = 0;
        }
        else
        {
            _field[fi].Val -= 1;
        }
    }

    private void RotateAgent(int idx, int v)
    {
        _agents[idx].D += v;
        _agents[idx].D %= 4;
    }

    private void MoveForward(int idx)
    {
        var i = _agents[idx].I;
        var j = _agents[idx].J;
        var k = _agents[idx].K;
        var d = _agents[idx].D;
        var jj = j + Dj[d];
        var kk = k + Dk[d];
        if (jj >= N)
        {
            _agents[idx].I = i / 3 * 3 + (i % 3 + 1) % 3;  // [1, 2, 0, 4, 5, 3][i];
            _agents[idx].J = k;
            _agents[idx].K = N - 1;
            _agents[idx].D = 3;
        }
        else if (jj < 0)
        {
            _agents[idx].I = (1 - i / 3) * 3 + (4 - i % 3) % 3;  // [4, 3, 5, 1, 0, 2][i];
            _agents[idx].J = 0;
            _agents[idx].K = N - 1 - k;
            _agents[idx].D = 0;
        }
        else if (kk >= N)
        {
            _agents[idx].I = i / 3 * 3 + (i % 3 + 2) % 3;  // [2, 0, 1, 5, 3, 4][i];
            _agents[idx].J = N - 1;
            _agents[idx].K = j;
            _agents[idx].D = 2;
        }
        else if (kk < 0)
        {
            _agents[idx].I = (1 - i / 3) * 3 + (3 - i % 3) % 3;  // [3, 5, 4, 0, 2, 1][i];
            _agents[idx].J = N - 1 - j;
            _agents[idx].K = 0;
            _agents[idx].D = 1;
        }
        else
        {
            _agents[idx].J = jj;
            _agents[idx].K = kk;
        }
    }

    private static int FieldIdx(int i, int j, int k)
    {
        return (i * N + j) * N + k;
    }

    private struct Agent
    {
        public int I;
        public int J;
        public int K;
        public int D;
    }

    private struct Cell
    {
        public int Owner;
        public int Val;
    }
}
