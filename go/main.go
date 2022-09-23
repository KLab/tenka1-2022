package main

import (
	"encoding/json"
	"fmt"
	"io/ioutil"
	"log"
	"math/rand"
	"net/http"
	"os"
	"strconv"
	"time"
)

var GameServer = "https://2022contest.gbc.tenka1.klab.jp"
var TOKEN = "YOUR_TOKEN"

const N = 5

var Dj = []int{+1, 0, -1, 0}
var Dk = []int{0, +1, 0, -1}

// 初期化処理
func init() {
	rand.Seed(time.Now().Unix())

	if os.Getenv("GAME_SERVER") != "" {
		GameServer = os.Getenv("GAME_SERVER")
	}
	if os.Getenv("TOKEN") != "" {
		TOKEN = os.Getenv("TOKEN")
	}
}

// game_idを取得する
// 環境変数で指定されていない場合は練習試合のgame_idを返す
func getGameId() (int64, error) {
	// 環境変数にGAME_IDが設定されている場合これを優先する
	if os.Getenv("GAME_ID") != "" {
		return strconv.ParseInt(os.Getenv("GAME_ID"), 10, 64)
	}

	// start APIを呼び出し練習試合のgame_idを取得する
	mode := 0
	delay := 0
	res, err := callAPI(fmt.Sprintf("/api/start/%s/%d/%d", TOKEN, mode, delay))
	if err != nil {
		return 0, err
	}

	var start struct {
		Status string `json:"status"`
		Start  int64  `json:"start"`
		GameId int64  `json:"game_id"`
	}

	err = json.Unmarshal(res, &start)
	if err != nil {
		return 0, err
	}

	if start.Status == "ok" || start.Status == "started" {
		return start.GameId, nil
	} else {
		return 0, fmt.Errorf("Start Api Error %#v", start)
	}
}

// 移動APIのレスポンス用の構造体
type Move struct {
	Status string      `json:"status"`
	Now    int64       `json:"now"`
	Turn   int         `json:"turn"`
	Move   []int       `json:"move"`
	Score  []int       `json:"score"`
	Field  [][][][]int `json:"field"`
	Agent  [][]int     `json:"agent"`
}

// ゲームサーバのAPIを叩く
func callAPI(x string) ([]byte, error) {
	url := GameServer + x
	// 5xxエラーの際は100ms空けて5回までリトライする
	for i := 0; i < 5; i++ {
		fmt.Println(url)
		resp, err := http.Get(url)
		if err != nil {
			return nil, err
		}
		//goland:noinspection GoUnhandledErrorResult
		defer resp.Body.Close()
		body, err := ioutil.ReadAll(resp.Body)
		if 500 <= resp.StatusCode && resp.StatusCode < 600 {
			fmt.Println(resp.Status)
			time.Sleep(time.Millisecond * 100)
			continue
		}
		if resp.StatusCode != 200 {
			return nil, fmt.Errorf(resp.Status)
		}
		return body, err
	}
	return nil, fmt.Errorf("Api Error")
}

// dir方向に移動するように移動APIを呼ぶ
func callMove(gameId int64, dir int) (*Move, error) {
	res, err := callAPI(fmt.Sprintf("/api/move/%s/%d/%d", TOKEN, gameId, dir))
	if err != nil {
		return nil, err
	}
	var move Move
	err = json.Unmarshal(res, &move)
	return &move, err
}

// idxのエージェントがいる位置のFieldを更新する
func (m *Move) Paint(idx int) {
	i := m.Agent[idx][0]
	j := m.Agent[idx][1]
	k := m.Agent[idx][2]
	if m.Field[i][j][k][0] == -1 {
		// 誰にも塗られていない場合はidxのエージェントで塗る
		m.Field[i][j][k][0] = idx
		m.Field[i][j][k][1] = 2
	} else if m.Field[i][j][k][0] == idx {
		// idxのエージェントで塗られている場合は完全に塗られた状態に上書きする
		m.Field[i][j][k][1] = 2
	} else if m.Field[i][j][k][1] == 1 {
		// idx以外のエージェントで半分塗られた状態の場合は誰にも塗られていない状態にする
		m.Field[i][j][k][0] = -1
		m.Field[i][j][k][1] = 0
	} else {
		// idx以外のエージェントで完全に塗られた状態の場合は半分塗られた状態にする
		m.Field[i][j][k][1] -= 1
	}
}

// エージェントidxをd方向に回転させる
// 方向については問題概要に記載しています
func (m *Move) RotateAgent(idx int, d int) {
	m.Agent[idx][3] += d
	m.Agent[idx][3] %= 4
}

// idxのエージェントを前進させる
// マス(i, j, k)については問題概要に記載しています
func (m *Move) MoveForward(idx int) {
	i := m.Agent[idx][0]
	j := m.Agent[idx][1]
	k := m.Agent[idx][2]
	d := m.Agent[idx][3]
	var jj = j + Dj[d]
	var kk = k + Dk[d]
	if jj >= N {
		m.Agent[idx][0] = i/3*3 + (i%3+1)%3 // [1, 2, 0, 4, 5, 3][i]
		m.Agent[idx][1] = k
		m.Agent[idx][2] = N - 1
		m.Agent[idx][3] = 3
	} else if jj < 0 {
		m.Agent[idx][0] = (1-i/3)*3 + (4-i%3)%3 // [4, 3, 5, 1, 0, 2][i]
		m.Agent[idx][1] = 0
		m.Agent[idx][2] = N - 1 - k
		m.Agent[idx][3] = 0
	} else if kk >= N {
		m.Agent[idx][0] = i/3*3 + (i%3+2)%3 // [2, 0, 1, 5, 3, 4][i]
		m.Agent[idx][1] = N - 1
		m.Agent[idx][2] = j
		m.Agent[idx][3] = 2
	} else if kk < 0 {
		m.Agent[idx][0] = (1-i/3)*3 + (3-i%3)%3 // [3, 5, 4, 0, 2, 1][i]
		m.Agent[idx][1] = N - 1 - j
		m.Agent[idx][2] = 0
		m.Agent[idx][3] = 1
	} else {
		m.Agent[idx][1] = jj
		m.Agent[idx][2] = kk
	}
}

// エージェントが同じマスにいるかを判定する
func IsSamePos(a []int, b []int) bool {
	return a[0] == b[0] && a[1] == b[1] && a[2] == b[2]
}

// idxのエージェントがいるマスが自分のエージェントで塗られているかを判定する
func (m *Move) IsOwnedCell(idx int) bool {
	i := m.Agent[idx][0]
	j := m.Agent[idx][1]
	k := m.Agent[idx][2]
	return m.Field[i][j][k][0] == idx
}

// 全エージェントの移動方向の配列を受け取り移動させてフィールドを更新する
// -1の場合は移動させません(0~3は移動APIのドキュメント記載と同じです)
func (m *Move) CopyAndMove(move []int) *Move {
	s, err := json.Marshal(m)
	if err != nil {
		log.Fatal(err)
	}
	var res Move
	err = json.Unmarshal(s, &res)
	if err != nil {
		log.Fatal(err)
	}
	// エージェントの移動処理
	for idx := 0; idx < 6; idx++ {
		if move[idx] == -1 {
			continue
		}
		res.RotateAgent(idx, move[idx])
		res.MoveForward(idx)
	}

	// フィールドの更新処理
	for idx := 0; idx < 6; idx++ {
		if move[idx] == -1 {
			continue
		}
		ok := true
		for j := 0; j < 6; j++ {
			if idx == j || move[j] == -1 || !IsSamePos(res.Agent[idx], res.Agent[j]) || res.IsOwnedCell(idx) {
				continue
			}
			// 移動した先にidx以外のエージェントがいる場合は修復しか行えないのでidxのエージェントのマスではない場合は更新しないようにフラグをfalseにする
			ok = false
			break
		}

		if !ok {
			continue
		}
		res.Paint(idx)
	}
	return &res
}

type Bot struct {
}

func NewBot() *Bot {
	return &Bot{}
}

// BOTのメイン処理
func (bot *Bot) solve() {
	gameId, err := getGameId()
	if err != nil {
		log.Fatal(err)
	}

	dir := rand.Intn(4)
	for {
		// 移動APIを呼ぶ
		move, err := callMove(gameId, dir)
		if err != nil {
			log.Fatal(err)
		}
		log.Printf("status = %s\n", move.Status)
		if move.Status == "already_moved" {
			continue
		} else if move.Status != "ok" {
			break
		}
		log.Printf("turn = %d", move.Turn)
		log.Printf("score = %d %d %d %d %d %d", move.Score[0], move.Score[1], move.Score[2], move.Score[3], move.Score[4], move.Score[5])
		// 4方向で移動した場合を全部シミュレーションする
		bestC := -1
		var bestD []int
		for d := 0; d < 4; d++ {
			m := move.CopyAndMove([]int{d, -1, -1, -1, -1, -1})
			// 自身のエージェントで塗られているマス数をカウントする
			c := 0
			for i := 0; i < 6; i++ {
				for j := 0; j < N; j++ {
					for k := 0; k < N; k++ {
						if m.Field[i][j][k][0] == 0 {
							c++
						}
					}
				}
			}
			// 最も多くのマスを自身のエージェントで塗れる移動方向のリストを保持する
			if c > bestC {
				bestC = c
				bestD = make([]int, 0, 4)
				bestD = append(bestD, d)
			} else if c == bestC {
				bestD = append(bestD, d)
			}
		}
		// 最も多くのマスを自身のエージェントで塗れる移動方向のリストからランダムで方向を決める
		dir = bestD[rand.Intn(len(bestD))]
	}
}

func main() {
	bot := NewBot()
	bot.solve()
}
