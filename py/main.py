import os
import sys
import random
import json
import time
import urllib.request
import urllib.error
from copy import deepcopy

# ゲームサーバのアドレス / トークン
GAME_SERVER = os.getenv('GAME_SERVER', 'https://2022contest.gbc.tenka1.klab.jp')
TOKEN = os.getenv('TOKEN', 'YOUR_TOKEN')

N = 5
Dj = [+1, 0, -1, 0]
Dk = [0, +1, 0, -1]


# ゲームサーバのAPIを叩く
def call_api(x: str) -> dict:
    url = f'{GAME_SERVER}{x}'
    # 5xxエラーの際は100ms空けて5回までリトライする
    for i in range(5):
        print(url, flush=True)
        try:
            with urllib.request.urlopen(url) as res:
                return json.loads(res.read())
        except urllib.error.HTTPError as err:
            if 500 <= err.code and err.code < 600:
                print(err.code)
                time.sleep(0.1)
                continue
            else:
                raise
        except ConnectionResetError as err:
            print(err)
            time.sleep(0.1)
            continue
    raise Exception('Api Error')


# game_idを取得する
# 環境変数で指定されていない場合は練習試合のgame_idを返す
def get_game_id() -> int:
    # 環境変数にGAME_IDが設定されている場合これを優先する
    if os.getenv('GAME_ID'):
        return int(os.getenv('GAME_ID'))

    # start APIを呼び出し練習試合のgame_idを取得する
    mode = 0
    delay = 0

    start = call_api(f'/api/start/{TOKEN}/{mode}/{delay}')
    if start['status'] == 'ok' or start['status'] == 'started':
        return start['game_id']

    raise Exception(f'Start Api Error : {start}')


# d方向に移動するように移動APIを呼ぶ
def call_move(game_id: int, d: int) -> dict:
    return call_api(f'/api/move/{TOKEN}/{game_id}/{d}')


# ゲーム状態クラス
class State:
    def __init__(self, field, agent):
        self.field = deepcopy(field)
        self.agent = deepcopy(agent)

    # idxのエージェントがいる位置のfieldを更新する
    def paint(self, idx: int):
        i, j, k, _ = self.agent[idx]
        if self.field[i][j][k][0] == -1:
            # 誰にも塗られていない場合はidxのエージェントで塗る
            self.field[i][j][k][0] = idx
            self.field[i][j][k][1] = 2
        elif self.field[i][j][k][0] == idx:
            # idxのエージェントで塗られている場合は完全に塗られた状態に上書きする
            self.field[i][j][k][1] = 2
        elif self.field[i][j][k][1] == 1:
            # idx以外のエージェントで半分塗られた状態の場合は誰にも塗られていない状態にする
            self.field[i][j][k][0] = -1
            self.field[i][j][k][1] = 0
        else:
            # idx以外のエージェントで完全に塗られた状態の場合は半分塗られた状態にする
            self.field[i][j][k][1] -= 1

    # エージェントidxをd方向に回転させる
    # 方向については問題概要に記載しています
    def rotate_agent(self, idx: int, d: int):
        self.agent[idx][3] += d
        self.agent[idx][3] %= 4

    # idxのエージェントを前進させる
    # マス(i, j, k)については問題概要に記載しています
    def move_forward(self, idx: int):
        i, j, k, d = self.agent[idx]
        jj = j + Dj[d]
        kk = k + Dk[d]
        if jj >= N:
            self.agent[idx][0] = i // 3 * 3 + (i % 3 + 1) % 3  # [1, 2, 0, 4, 5, 3][i]
            self.agent[idx][1] = k
            self.agent[idx][2] = N - 1
            self.agent[idx][3] = 3
        elif jj < 0:
            self.agent[idx][0] = (1 - i // 3) * 3 + (4 - i % 3) % 3  # [4, 3, 5, 1, 0, 2][i]
            self.agent[idx][1] = 0
            self.agent[idx][2] = N - 1 - k
            self.agent[idx][3] = 0
        elif kk >= N:
            self.agent[idx][0] = i // 3 * 3 + (i % 3 + 2) % 3  # [2, 0, 1, 5, 3, 4][i]
            self.agent[idx][1] = N - 1
            self.agent[idx][2] = j
            self.agent[idx][3] = 2
        elif kk < 0:
            self.agent[idx][0] = (1 - i // 3) * 3 + (3 - i % 3) % 3  # [3, 5, 4, 0, 2, 1][i]
            self.agent[idx][1] = N - 1 - j
            self.agent[idx][2] = 0
            self.agent[idx][3] = 1
        else:
            self.agent[idx][1] = jj
            self.agent[idx][2] = kk

    # エージェントが同じマスにいるかを判定する
    def is_same_pos(self, a: [int], b: [int]) -> bool:
        return a[0] == b[0] and a[1] == b[1] and a[2] == b[2]

    # idxのエージェントがいるマスが自分のエージェントで塗られているかを判定する
    def is_owned_cell(self, idx: int) -> bool:
        i = self.agent[idx][0]
        j = self.agent[idx][1]
        k = self.agent[idx][2]
        return self.field[i][j][k][0] == idx

    # 全エージェントの移動方向の配列を受け取り移動させてフィールドを更新する
    # -1の場合は移動させません(0~3は移動APIのドキュメント記載と同じです)
    def move(self, move: [int]):
        # エージェントの移動処理
        for idx in range(6):
            if move[idx] == -1:
                continue
            self.rotate_agent(idx, move[idx])
            self.move_forward(idx)

        # フィールドの更新処理
        for idx in range(6):
            if move[idx] == -1:
                continue
            ok = True
            for j in range(6):
                if idx == j or move[j] == -1 or not self.is_same_pos(self.agent[idx], self.agent[j]) or self.is_owned_cell(idx):
                    continue
                # 移動した先にidx以外のエージェントがいる場合は修復しか行えないのでidxのエージェントのマスではない場合は更新しないようにフラグをfalseにする
                ok = False
                break

            if not ok:
                continue
            self.paint(idx)


class Bot:
    def solve(self):
        game_id = get_game_id()
        next_d = random.randint(0, 3)
        while True:
            # 移動APIを呼ぶ
            move = call_move(game_id, next_d)
            print('status = {}'.format(move['status']), file=sys.stderr, flush=True)
            if move['status'] == "already_moved":
                continue
            elif move['status'] != 'ok':
                break
            print('turn = {}'.format(move['turn']), file=sys.stderr, flush=True)
            print('score = {} {} {} {} {} {}'.format(move['score'][0], move['score'][1], move['score'][2], move['score'][3], move['score'][4], move['score'][5]), file=sys.stderr, flush=True)
            # 4方向で移動した場合を全部シミュレーションする
            best_c = -1
            best_d = []
            for d in range(4):
                m = State(move['field'], move['agent'])
                m.move([d, -1, -1, -1, -1, -1])
                # 自身のエージェントで塗られているマス数をカウントする
                c = 0
                for i in range(6):
                    for j in range(N):
                        for k in range(N):
                            if m.field[i][j][k][0] == 0:
                                c += 1
                # 最も多くのマスを自身のエージェントで塗れる移動方向のリストを保持する
                if c > best_c:
                    best_c = c
                    best_d = [d]
                elif c == best_c:
                    best_d.append(d)
            # 最も多くのマスを自身のエージェントで塗れる移動方向のリストからランダムで方向を決める
            next_d = random.choice(best_d)


if __name__ == "__main__":
    bot = Bot()
    bot.solve()
