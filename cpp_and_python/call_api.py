import json
import os
import subprocess
import sys
import time
import urllib.request
import urllib.error

# ゲームサーバのアドレス / トークン
GAME_SERVER = os.getenv('GAME_SERVER', 'https://2022contest.gbc.tenka1.klab.jp')
TOKEN = os.getenv('TOKEN', 'YOUR_TOKEN')

p = subprocess.Popen(sys.argv[1:], stdin=subprocess.PIPE, stdout=subprocess.PIPE)


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


class Bot:
    def solve(self):
        game_id = get_game_id()
        while True:
            line = p.stdout.readline()
            if not line:
                break
            next_d = int(line.decode())

            # 移動APIを呼ぶ
            move = call_move(game_id, next_d)
            print('status = {}'.format(move['status']), file=sys.stderr, flush=True)
            p.stdin.write(f'{move["status"]}\n'.encode())
            if move['status'] != 'ok':
                p.stdin.flush()
                break
            p.stdin.write(f'{move["now"]} {move["turn"]}\n'.encode())
            assert len(move['move']) == 6
            p.stdin.write((' '.join(str(x) for x in move['move']) + '\n').encode())
            assert len(move['score']) == 6
            p.stdin.write((' '.join(str(x) for x in move['score']) + '\n').encode())
            assert len(move['field']) == 6
            for face in move['field']:
                assert len(face) == 5
                for row in face:
                    assert len(row) == 5
                    for cell in row:
                        assert len(cell) == 2
                        p.stdin.write(f'{cell[0]} {cell[1]}\n'.encode())
            assert len(move['agent']) == 6
            for agent in move['agent']:
                assert len(agent) == 4
                p.stdin.write((' '.join(str(x) for x in agent) + '\n').encode())
            p.stdin.flush()


if __name__ == "__main__":
    bot = Bot()
    bot.solve()
