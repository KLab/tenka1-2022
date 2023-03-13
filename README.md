# 天下一 Game Battle Contest 2022

- [公式サイト](https://tenka1.klab.jp/2022/)
- [YouTube配信](https://www.youtube.com/watch?v=JNwDmtjbu0A)

## 問題概要

- [問題概要およびAPI仕様](problem.md)
- [チュートリアル](tutorial.md)
- [Runnerの使い方](runner.md)
- [ビジュアライザの使い方](visualizer.md)

## サンプルコード

- [Go](go)
- [Python](py)
- [C#](cs)
- [Rust](rust)
- [C++(libcurl)](cpp) 通信にライブラリを使用
- [C++(Python)](cpp_and_python) 通信にPythonを使用


動作確認環境はいずれも Ubuntu 20.04 LTS

## コンテスト結果

### 予選
- [予選終了時のランキング](result/qual.tsv)

### 決勝

- [決勝の各ゲームの結果](result/final.tsv)

| 順位 | ユーザID | 順位点合計 |
| - | - | - |
| 1位 | tardigrade | 19 |
| 2位 | eijirou | 17 |
| 3位 | fuppy | 9 |
| 4位 | penpenpen | 5 |
| 5位 | yokozuna57 | 3 |
| 6位 | WA_TLE | -9 |
| 7位 | wanui | -21 |
| 8位 | bono | -23 |

## ゲームサーバのプログラム

- [APIサーバとマッチングサーバ](game)

## ローカル実行
ゲームサーバーを手元で動かせる環境を用意しました。

docker をインストールした環境で、以下のコマンドを実行してください。

起動
```
$ docker compose up
```

ユーザー登録
```
# ユーザID: user0001 トークン: token0001 のユーザーを作成
$ docker compose exec gamedb redis-cli HSET user_token token0001 user0001
```

以下のURLでAPIとビジュアライザにアクセス可能です。
- http://localhost:8008/api/move/token0001/1/1
- http://localhost:8008/visualizer/index.html?token=token0001

Runnerを使用する場合は、Runnerを起動して設定を以下のように変更します。
- GameServer: `localhost:8008`

## ビジュアライザで使用したライブラリ等

- ビジュアライザ本体 © 2022 KLab Inc. All rights reserved.
- Game BGM and SE by KLab Sound Team © 2022 KLab Inc. All rights reserved.
- [DOTween © 2014 Daniele Giardini - Demigiant](http://dotween.demigiant.com)
- [Hurisake.JsonDeserializer by hasipon](https://github.com/hasipon/Hurisake.JsonDeserializer)
- [Rajdhani (OFL) © Indian Type Foundry](https://fonts.google.com/specimen/Rajdhani)
- [Bootstrap Icons (MIT) © The Bootstrap Authors](https://github.com/twbs/icons)
- [Rank 3 icon (CC BY 3.0) © Skoll](https://game-icons.net/1x1/skoll/rank-3.html)

## ルール

- コンテスト期間
  - 2022年9月23日(金・祝)
    - 予選リーグ: ~~14:00～18:00~~ 14:00～18:30
    - 決勝リーグ: ~~18:00～18:20~~ 18:30～18:50
    - ※予選リーグ終了後、上位8名による決勝リーグを開催
    - **ゲームが正常に動いていない時間があったため予選リーグ・決勝リーグの時間を変更しております**
- 参加資格
  - 学生、社会人問わず、どなたでも参加可能です。他人と協力せず、個人で取り組んでください。
- 使用可能言語
  - 言語の制限はありません。ただしHTTPSによる通信ができる必要があります。
- SNS等の利用について
  - 本コンテスト開催中にSNS等にコンテスト問題について言及して頂いて構いませんが、ソースコードを公開するなどの直接的なネタバレ行為はお控えください。
ハッシュタグ: #klabtenka1

## その他

- [ギフトカード抽選プログラム](lottery)
  - 抽選対象は join API で開始したゲームに一度以上 move API を実行した参加者です
- [joinで開始した各ゲームのmoveデータ](result/moves.zip) (zip)
