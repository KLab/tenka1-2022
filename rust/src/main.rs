use std::{thread, time};
use anyhow::Result;
use rand::prelude::*;
use serde::{Serialize, Deserialize};
use thiserror::Error;

const N: i32 = 5;
const DJ: [i32; 4] = [1, 0, -1, 0];
const DK: [i32; 4] = [0, 1, 0, -1];

/// 練習試合開始APIのレスポンス用の構造体
#[derive(Serialize, Deserialize)]
struct StartResponse {
    status: StartStatus,
    game_id: i32,
    start: i64,
}

/// 練習試合の状態
#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
enum StartStatus {
    Ok,
    Started,

    #[serde(other)]
    Unknown,
}

/// 移動APIのレスポンス用の構造体
#[derive(Serialize, Deserialize)]
struct MoveResponse {
    status: MoveStatus,
    #[serde(default)]
    now: i64,
    #[serde(default)]
    turn: i32,
    #[serde(rename = "move", default)]
    agent_move: [i32; 6],
    #[serde(default)]
    score: [i32; 6],
    #[serde(default)]
    field: [[[[i32; 2]; 5]; 5]; 6],
    #[serde(default)]
    agent: [[i32; 4]; 6],
}

/// 移動APIの結果
#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MoveStatus {
    Ok,
    AlreadyMoved,
    GameFinished,

    #[serde(other)]
    Unknown,
}

/// ゲーム状態構造体
#[derive(Clone)]
struct State {
    field: [[[[i32; 2]; 5]; 5]; 6],
    agent: [[i32; 4]; 6],
}

impl State {
    /// idxのエージェントがいる位置のfieldを更新する
    fn paint(&mut self, idx: usize) {
        let i = self.agent[idx][0] as usize;
        let j = self.agent[idx][1] as usize;
        let k = self.agent[idx][2] as usize;
        if self.field[i][j][k][0] == -1 {
            // 誰にも塗られていない場合はidxのエージェントで塗る
            self.field[i][j][k][0] = idx as i32;
            self.field[i][j][k][1] = 2;
        } else if self.field[i][j][k][0] == idx as i32 {
            // idxのエージェントで塗られている場合は完全に塗られた状態に上書きする
            self.field[i][j][k][1] = 2;
        } else if self.field[i][j][k][1] == 1 {
            // idx以外のエージェントで半分塗られた状態の場合は誰にも塗られていない状態にする
            self.field[i][j][k][0] = -1;
            self.field[i][j][k][1] = 0;
        } else {
            // idx以外のエージェントで完全に塗られた状態の場合は半分塗られた状態にする
            self.field[i][j][k][1] -= 1;
        }
    }

    /// エージェントidxをd方向に回転させる
    /// 方向については問題概要に記載しています
    fn rotate_agent(&mut self, idx: usize, d: i32) {
        self.agent[idx][3] += d;
        self.agent[idx][3] %= 4;
    }

    /// idxのエージェントを前進させる
    /// マス(i, j, k)については問題概要に記載しています
    fn move_forward(&mut self, idx: usize) {
        let i = self.agent[idx][0];
        let j = self.agent[idx][1];
        let k = self.agent[idx][2];
        let d = self.agent[idx][3];
        let jj = j + DJ[d as usize];
        let kk = k + DK[d as usize];
        if jj >= N {
            self.agent[idx][0] = i / 3 * 3 + (i % 3 + 1) % 3;  // [1, 2, 0, 4, 5, 3][i]
            self.agent[idx][1] = k;
            self.agent[idx][2] = N - 1;
            self.agent[idx][3] = 3;
        } else if jj < 0 {
            self.agent[idx][0] = (1 - i / 3) * 3 + (4 - i % 3) % 3;  // [4, 3, 5, 1, 0, 2][i]
            self.agent[idx][1] = 0;
            self.agent[idx][2] = N - 1 - k;
            self.agent[idx][3] = 0;
        } else if kk >= N {
            self.agent[idx][0] = i / 3 * 3 + (i % 3 + 2) % 3;  // [2, 0, 1, 5, 3, 4][i]
            self.agent[idx][1] = N - 1;
            self.agent[idx][2] = j;
            self.agent[idx][3] = 2;
        } else if kk < 0 {
            self.agent[idx][0] = (1 - i / 3) * 3 + (3 - i % 3) % 3;  // [3, 5, 4, 0, 2, 1][i]
            self.agent[idx][1] = N - 1 - j;
            self.agent[idx][2] = 0;
            self.agent[idx][3] = 1;
        } else {
            self.agent[idx][1] = jj;
            self.agent[idx][2] = kk;
        }
    }

    /// エージェントが同じマスにいるかを判定する
    fn is_same_pos(&self, a: [i32; 4], b: [i32; 4]) -> bool {
        &a[0..3] == &b[0..3]
    }
    
    /// idxのエージェントがいるマスが自分のエージェントで塗られているかを判定する
    fn is_owned_cell(&self, idx: usize) -> bool {
        let i = self.agent[idx][0] as usize;
        let j = self.agent[idx][1] as usize;
        let k = self.agent[idx][2] as usize;
        return self.field[i][j][k][0] == idx as i32;
    }

    /// 全エージェントの移動方向の配列を受け取り移動させてフィールドを更新する
    /// -1の場合は移動させません(0~3は移動APIのドキュメント記載と同じです)
    fn move_agent(&mut self, move_dir: [i32; 6]) {
        for (idx, d) in move_dir.iter().copied().enumerate().filter(|&(_, d)| d != -1) {
            // エージェントの移動処理
            self.rotate_agent(idx, d);
            self.move_forward(idx);

            // フィールドの更新処理
            // 移動した先にidx以外のエージェントがいる場合は修復しか行えないのでidxのエージェントのマスではない場合は更新しないようにする
            let ok = (0..6).all(|j| idx == j || move_dir[j] == -1 || !self.is_same_pos(self.agent[idx], self.agent[j]) || self.is_owned_cell(idx));
            if ok {
                self.paint(idx);
            }
        }
    }
}

#[derive(Error, Debug)]
pub enum ApiError {
    #[error("Start Api Error : {0}")]
    StartApiError(String),
    #[error("Api Error")]
    ApiError(),
}

struct Bot {
    game_server: String,
    token: String,
}

impl Bot {
    /// ゲームサーバのAPIを叩く
    fn call_api(&self, x: String) -> Result<reqwest::blocking::Response> {
        let url = format!("{}{}", self.game_server, x);

        // 5xxエラーの際は100ms空けて5回までリトライする
        for _d in 0..5 {
            println!("{url}");

            let response = reqwest::blocking::get(&url)?;

            if response.status().is_server_error() {
                println!("{}", response.status());
                let sleep_time = time::Duration::from_millis(100);
                thread::sleep(sleep_time);
                continue
            }

            return Ok(response);
        }

        Err(ApiError::ApiError())?
    }

    /// game_idを取得する
    /// 環境変数で指定されていない場合は練習試合のgame_idを返す
    fn get_game_id(&self) -> Result<i32> {
        // 環境変数にGAME_IDが設定されている場合これを優先する
        if let Ok(val) = std::env::var("GAME_ID") {
            return Ok(val.parse()?);
        }

        // start APIを呼び出し練習試合のgame_idを取得する
        let mode = 0;
        let delay = 0;
    
        let start_response: StartResponse = self.call_api(format!("/api/start/{}/{}/{}", self.token, mode, delay))?.json()?;
        match start_response.status {
            StartStatus::Ok | StartStatus::Started => Ok(start_response.game_id),
            StartStatus::Unknown => Err(ApiError::StartApiError(serde_json::to_string(&start_response)?).into()),
        }
    }

    /// d方向に移動するように移動APIを呼ぶ
    fn call_move(&self, game_id: i32, d: i32) -> Result<MoveResponse> {
        let move_response: MoveResponse = self.call_api(format!("/api/move/{}/{}/{}", self.token, game_id, d))?.json()?;
        Ok(move_response)
    }

    /// BOTのメイン処理
    fn solve(&self) -> Result<()> {
        let mut rng = thread_rng();
        let game_id = self.get_game_id()?;
        let mut next_d = rng.gen_range(0..4);
        loop {
            // 移動APIを呼ぶ
            let move_response = self.call_move(game_id, next_d)?;
            println!("status = {:?}", move_response.status);
            match move_response.status {
                MoveStatus::AlreadyMoved => continue,
                MoveStatus::Ok => (),
                _ => break,
            }
            println!("turn = {}", move_response.turn);
            println!("score = {:?}", move_response.score);
            // 4方向で移動した場合を全部シミュレーションする
            let mut best_c = -1;
            let mut best_d = [0, 0, 0, 0];
            let mut best_d_len = 0;
            for d in 0..4 {
                let mut m = State { field: move_response.field, agent: move_response.agent };
                m.move_agent([d, -1, -1, -1, -1, -1]);
                // 自身のエージェントで塗られているマス数をカウントする
                let mut c = 0;
                for i in 0..6 {
                    for j in 0..N {
                        for k in 0..N {
                            if m.field[i as usize][j as usize][k as usize][0] == 0 {
                                c += 1;
                            }
                        }
                    }
                }
                // 最も多くのマスを自身のエージェントで塗れる移動方向のリストを保持する
                if c > best_c {
                    best_c = c;
                    best_d[0] = d;
                    best_d_len = 1;
                } else if c == best_c {
                    best_d[best_d_len] = d;
                    best_d_len += 1;
                }
            }
            // 最も多くのマスを自身のエージェントで塗れる移動方向のリストからランダムで方向を決める
            next_d = *best_d[..best_d_len].choose(&mut rng).unwrap_or(&next_d);
        }

        Ok(())
    }
}

fn main() {
    let bot = Bot { 
        game_server: std::env::var("GAME_SERVER").unwrap_or_else(|_| "https://2022contest.gbc.tenka1.klab.jp".to_string()),
        token: std::env::var("TOKEN").unwrap_or_else(|_| "YOUR_TOKEN".to_string()),
    };
    match bot.solve() {
        Ok(_) => println!("finish"),
        Err(msg) => panic!("failure: {}", msg),
    }
}