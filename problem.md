問題概要
======

これはエージェントを操作して 1 面が 5×5 の立方体のフィールドを塗っていき、塗ったマスの面積を競う 6 人対戦のゲームです。

1 ゲームは 294 ターンで、1 ターンは 500 ミリ秒です。

後半 147 ターンでの、塗られた状態のマスの面積の累積合計によってゲームごとの順位を決定します。

エージェントには向きがあり、各ターンで、下記のいずれかの行動ができます。

- 回転せず 1 マス前進する
- 左に90度回転して 1 マス前進する
- 180度回転して 1 マス前進する
- 右に90度回転して 1 マス前進する

エージェントが移動したとき、以下のようにマスの状態が変化します。

- 【取得】移動先のマスが誰にも塗られていない状態の場合、自エージェントによって完全に塗られた状態になる
- 【半壊】移動先のマスが他エージェントによって完全に塗られた状態の場合、半分塗られた状態になる（塗ったエージェントは変わらない）
- 【全壊】移動先のマスが他エージェントによって半分塗られた状態の場合、誰にも塗られていない状態になる
- 【修復】移動先のマスが自エージェントによって半分塗られた状態の場合、自エージェントによって完全に塗られた状態になる

ただし、複数のエージェントが同じターンに同一のマスに移動した場合、【取得】【半壊】【全壊】は行われませんが、【修復】は行われます。

塗ったマスの面積としては、完全に塗られた状態であっても半分塗られた状態であっても 1 マスと数えます。

## マスの座標

各マスを `(i, j, k)` で表します。(`i` は 0 以上 5 以下、 `j`, `k` は 0 以上 4 以下)

`i` はどの面かを表し、下記の通りに配置されます。
左図はビジュアライザの表示に基づいており、上の立方体は鏡像反転していることにご注意ください。

<img alt="iの配置" src="img/problem_fig_i.png" width="480">

各面の マス `j, k` は、下記の通りに配置されます。また、方向 `d` は下記の通りに表します。

<img alt="j,kの配置" src="img/problem_fig_jkd.png" width="480">

ゲーム開始時、エージェントは各面の `(i, 2, 2)` に配置され、方向 `0` を向きます。

[移動API](#move-api-移動api) は、自エージェントの初期位置が `(0, 2, 2)` である座標に基づいたレスポンスを返します。

初期位置のマスは、そのエージェントによって完全に塗られた状態になります。

## 順位点

マッチングによって参加したゲームについて、各ゲームの順位に基づき、下記の順位点が与えられます。

| 順位 | 順位点 |
| - | - |
| 1 位 | +5 点 |
| 2 位 | +3 点 |
| 3 位 | +1 点 |
| 4 位 | -1 点 |
| 5 位 | -3 点 |
| 6 位 | -5 点 |

同点の場合、順位点は平均されて与えられます。

例えば、1位と2位が同点だった場合は両者に +4 点が与えられ、3位, 4位, 5位が同点だった場合は三者に -1 点ずつ与えられます。

## マッチングとクラス

マッチングは 150 秒ごとに行われます。

前回のマッチングと今回のマッチングの間に [マッチング参加API](#join-api-マッチング参加api) を実行していた参加者をマッチング対象とします。

マッチング参加APIを実行していない参加者はランキングから除外されます。

新規参加者はランキングの最下位に追加されます。

新規参加者については、先にマッチング参加APIを実行した参加者が上位となります。

順位に基づいて下記のようにクラスが決定されます。

| クラス | 順位 | 定員 |
| - | - | - |
| Class 1 | 1 - 12 位 | 12 名 |
| Class 2 | 13 - 24 位 | 12 名 |
| Class 3 | 25 - 42 位 | 18 名 |
| Class 4 | 43 - 66 位 | 24 名 |
| Class `n` | - | `6*n` 名 |

予選リーグでは、1 回のマッチングで、マッチング参加APIを実行した参加者が 1 人あたり 4 ゲームに参加するようゲームの参加者を決定します。

このとき、対戦相手は同じクラスから決定されます。

参加者が不足した場合、ランダムに行動するエージェントが追加されます。

## クラス内得点

ある参加者が、そのクラス内で

- 直近2マッチング (8ゲーム) の順位点合計が `x2`
- 直近3マッチング (12ゲーム) の順位点合計が `x3`
- 直近4マッチング (16ゲーム) の順位点合計が `x4`
- 直近5マッチング (20ゲーム) の順位点合計が `x5`

であるとき、`x2 / √2`, `x3 / √3`, `x4 / √4`, `x5 / √5` の最大値をクラス内得点とします。

このとき、新規参加や、クラスの昇格・降格によって、直近 n マッチングがすべて同一のクラスではなかった場合の順位点合計は考慮しません。

例えば、前々回のマッチングで Class 1 に昇格してそのマッチングの順位点合計が +5、前回のマッチングでは Class 1 で順位点合計が +5 だった場合、クラス内得点は `+10 / √2` となります

また、直近2マッチングが同一のクラスではなかった場合、クラス内得点は `N/A` (クラス内得点無し) となります

クラス内得点の計算の後、クラス内得点に基づいてクラス内の順位を決定します。

ただし、降格者はクラス内で上位であるとし、昇格者・新規参加者は下位であるとします。

また、同点の場合は前回のランキングで上位である方を上位とします。

クラス内の順位を決めたあと、クラス内得点が `N/A` でない人の中から、下記の人数に対して昇格・降格を行います。

| クラス | 昇格 | 降格 |
| - | - | - |
| Class 1 | 0 名 | 1 名 |
| Class 2 | 1 名 | 2 名 |
| Class 3 | 2 名 | 3 名 |
| Class 4 | 3 名 | 4 名 |
| Class `n` | `n-1` 名 | `n` 名 |

例えば、Class 2 のクラス内得点が `N/A` でない人のうち、上位 1 名が昇格し、下位 2 名が降格します。

Class `n` から昇格すると Class `n-1` に、Class `n` から降格すると Class `n+1` になります。

マッチング時の処理順は、クラス内得点の計算、昇格・降格処理、ランキング除外処理、順位確定・クラス決定処理、ゲーム参加者決定処理の順です。

そのため、降格の対象になった場合でも、他の参加者のランキング除外などの理由により実際には降格しないことがあります。

また、予選リーグの最後のマッチングでは、昇格・降格処理、ランキング除外処理は行われません。

## 決勝リーグ

予選リーグの上位 8 名が決勝リーグに進出します。

予選リーグと同じように、マッチング参加APIでgame_idが取得できるため、Runnerによって決勝リーグ進出者は自動的に決勝リーグに参加できます。

決勝リーグでは、7 回のマッチングを行い、1 回のマッチングで 1 人あたり 3 ゲームに参加するようゲームの参加者を決定します。

決勝リーグでの順位点合計に基づいて、天下一 Game Battle Contest 2022 の 1 位から 8 位を決定します。

同点の場合は予選リーグの順位に基づいて順位を決定します。

---

API仕様
======

以下の3種類のAPIを使用してゲームに参加します。

```
GET /api/move/{token}/{game_id}/{dir}
GET /api/start/{token}/{mode}/{delay}
GET /api/join/{token}
```

`{token}` は ポータルサイトトップに記載されています。  

ビジュアライザは以下のAPIを使用します。
```
GET /event/{token}
GET /api/data/{token}/{game_id}
```

`GET /event/{token}`, `GET /api/data/{token}/{game_id}` についてのドキュメントは提供しませんが、自作プログラムで使用することを禁止はしません。

## move API (移動API)

**endpoint**

```
GET /api/move/{token}/{game_id}/{dir}
```

- `{token}` : ポータルサイトに記載されているトークン
- `{game_id}` : ゲームID
- `{dir}` : 移動方向 (0以上3以下の整数)
  - dir = 0 : 前進
  - dir = 1 : 左に90度回転して前進
  - dir = 2 : 180度回転して前進
  - dir = 3 : 右に90度回転して前進

**response (成功時)**

`move`, `score`, `agent` は、最初の要素が自エージェントの情報を表します。
また、自エージェントの初期位置は `(i,j,k) = (0,2,2)` です。

`field[i][j][k]` はマス `(i,j,k)` の状態を表し、
誰にも塗られていない状態のとき `[-1,0]` 、
エージェント `x` によって完全に塗られた状態のとき `[x,2]` 、
エージェント `x` によって半分塗られた状態のとき `[x,1]` となります。

`agent[x]` はエージェント `x` の位置と方向を表し、マス `(i,j,k)` 方向 `d` のとき `[i,j,k,d]` となります。

座標については、 [マスの座標](#マスの座標) を参照ください。

```js
{
    "status": "ok",
    "now": 1658479876543,  // 時刻 [単位ミリ秒]
    "turn": 42,  // 現在のターン数
    "move": [1,1,2,2,3,3],  // そのターンの各エージェントの移動方向。移動しなかった場合は -1
    "score": [0,0,0,0,0,0],  // 後半 147 ターンでの、塗られた状態のマスの面積の累積合計
    "field": [[[[1,2],[2,2],[3,2],[4,1],[-1,0]], ...], ...],  // フィールドの情報。詳細は上記参照
    "agent": [[0,1,2,3], ...]  // エージェントの情報。詳細は上記参照
}
```

**response (失敗時: そのターンの移動APIが既に実行されている場合)**

```js
{
    "status": "already_moved"
}
```

**response (ゲーム終了時 : game_idのゲームが終了している場合)**

```js
{
    "status": "game_finished"
}
```

---
## start API (練習試合開始API)

**endpoint**

```
GET /api/start/{token}/{mode}/{delay}
```

- `{token}` : ポータルサイトに記載されているトークン
- `{mode}` : 練習試合のモード (0 または 1)
  - mode = 0 : 他のagentは移動しない
  - mode = 1 : 他のagentはランダムに移動する
- `{delay}` : 開始までの遅延時間 (単位秒, 0以上10以下の整数)

**response (成功時: 練習試合を新しく開始する場合)**

```js
{
    "status": "ok",
    "game_id": 10000,  // 練習試合のgame_id
    "start": 1656673673878  // 開始時刻 (UNIX時間 ミリ秒)
}
```

**response (成功時: 練習試合が既に進行中の場合)**

```js
{
    "status": "started",
    "game_id": 10000,  // 練習試合のgame_id
    "start": 1656673673878  // 開始時刻 (UNIX時間 ミリ秒)
}
```

**response (失敗時: 予選リーグ終了後の場合)**

```js
{
    "status": "game_finished"
}
```

---
## join API (マッチング参加API)

マッチング参加処理は参加者ごとに1,000ミリ秒に1回に制限されます。

**endpoint**
```
GET /api/join/{token}
```
- `{token}` : ポータルサイトに記載されているトークン

**response (成功時)**

新規参加者の場合、レスポンスの game_ids は空になりますが、join API実行によってマッチング対象となるため、繰り返し join API を実行することで、 game_id を得ることができます。

```js
{
    "status": "ok",
    "game_ids": [10000, 10001, 10002, 10003]  // マッチングによって開始された、進行中のゲームのgame_idのリスト
}
```

**response (失敗時: 処理開始を待っている間に新たにマッチング参加処理が行われた場合)**

```js
{
    "status": "error_time_limit"
}
```