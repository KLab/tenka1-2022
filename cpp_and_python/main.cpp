#include <iostream>
#include <vector>
#include <random>

using namespace std;

mt19937 mt;

const int N = 5;
const int Dj[] {+1, 0, -1, 0};
const int Dk[] {0, +1, 0, -1};

// 移動APIのレスポンス用の構造体
struct Move {
	string status;
	int64_t now;
	int turn;
	vector<int> move;
	vector<int> score;
	vector<vector<vector<vector<int>>>> field;
	vector<vector<int>> agent;

	// dir方向に移動するように移動APIを呼ぶ
	void call_move(int dir) {
		cout << dir << endl;
		cin >> status;
		if (status != "ok") return;
		cin >> now >> turn;
		move = vector<int>(6);
		score = vector<int>(6);
		field = vector<vector<vector<vector<int>>>>(6, vector<vector<vector<int>>>(5, vector<vector<int>>(5, vector<int>(2))));
		agent = vector<vector<int>>(6, vector<int>(4));
		for (auto& x : move) cin >> x;
		for (auto& x : score) cin >> x;
		for (auto& x : field) for (auto& y : x) for (auto& z : y) for (auto& w : z) cin >> w;
		for (auto& x : agent) for (auto& y : x) cin >> y;
	}

	// idxのエージェントがいる位置のFieldを更新する
	void paint(int idx) {
		int i = agent[idx][0];
		int j = agent[idx][1];
		int k = agent[idx][2];
		if (field[i][j][k][0] == -1) {
			// 誰にも塗られていない場合はidxのエージェントで塗る
			field[i][j][k][0] = idx;
			field[i][j][k][1] = 2;
		} else if (field[i][j][k][0] == idx) {
			// idxのエージェントで塗られている場合は完全に塗られた状態に上書きする
			field[i][j][k][1] = 2;
		} else if (field[i][j][k][1] == 1) {
			// idx以外のエージェントで半分塗られた状態の場合は誰にも塗られていない状態にする
			field[i][j][k][0] = -1;
			field[i][j][k][1] = 0;
		} else {
			// idx以外のエージェントで完全に塗られた状態の場合は半分塗られた状態にする
			field[i][j][k][1] -= 1;
		}
	}

	// エージェントidxをd方向に回転させる
	// 方向については問題概要に記載しています
	void rotate_agent(int idx, int d) {
		agent[idx][3] += d;
		agent[idx][3] %= 4;
	}

	// idxのエージェントを前進させる
	// マス(i, j, k)については問題概要に記載しています
	void move_forward(int idx) {
		int i = agent[idx][0];
		int j = agent[idx][1];
		int k = agent[idx][2];
		int d = agent[idx][3];
		int jj = j + Dj[d];
		int kk = k + Dk[d];
		if (jj >= N) {
			agent[idx][0] = i/3*3 + (i%3+1)%3; // [1, 2, 0, 4, 5, 3][i]
			agent[idx][1] = k;
			agent[idx][2] = N - 1;
			agent[idx][3] = 3;
		} else if (jj < 0) {
			agent[idx][0] = (1-i/3)*3 + (4-i%3)%3; // [4, 3, 5, 1, 0, 2][i]
			agent[idx][1] = 0;
			agent[idx][2] = N - 1 - k;
			agent[idx][3] = 0;
		} else if (kk >= N) {
			agent[idx][0] = i/3*3 + (i%3+2)%3; // [2, 0, 1, 5, 3, 4][i]
			agent[idx][1] = N - 1;
			agent[idx][2] = j;
			agent[idx][3] = 2;
		} else if (kk < 0) {
			agent[idx][0] = (1-i/3)*3 + (3-i%3)%3; // [3, 5, 4, 0, 2, 1][i]
			agent[idx][1] = N - 1 - j;
			agent[idx][2] = 0;
			agent[idx][3] = 1;
		} else {
			agent[idx][1] = jj;
			agent[idx][2] = kk;
		}
	}

	// エージェントが同じマスにいるかを判定する
	static bool is_same_pos(const vector<int>& a, const vector<int>& b) {
		return a[0] == b[0] && a[1] == b[1] && a[2] == b[2];
	}

	// idxのエージェントがいるマスが自分のエージェントで塗られているかを判定する
	bool is_owned_cell(int idx) const {
		int i = agent[idx][0];
		int j = agent[idx][1];
		int k = agent[idx][2];
		return field[i][j][k][0] == idx;
	}

	void update(const vector<int>& a) {
		// エージェントの移動処理
		for (int idx = 0; idx < 6; ++ idx) {
			if (move[idx] == -1) {
				continue;
			}
			rotate_agent(idx, move[idx]);
			move_forward(idx);
		}

		// フィールドの更新処理
		for (int idx = 0; idx < 6; ++ idx) {
			if (move[idx] == -1) {
				continue;
			}
			bool ok = true;
			for (int j = 0; j < 6; ++ j) {
				if (idx == j || move[j] == -1 || !is_same_pos(agent[idx], agent[j]) || is_owned_cell(idx)) {
					continue;
				}
				// 移動した先にidx以外のエージェントがいる場合は修復しか行えないのでidxのエージェントのマスではない場合は更新しないようにフラグをfalseにする
				ok = false;
				break;
			}

			if (!ok) {
				continue;
			}
			paint(idx);
		}
	}
};

struct Bot {
	void solve() {
		auto dir = uniform_int_distribution<>(0, 3)(mt);
		for (;;) {
			// 移動APIを呼ぶ
			Move move;
			move.call_move(dir);
			if (move.status != "ok") {
				break;
			}
			// 4方向で移動した場合を全部シミュレーションする
			int bestC = -1;
			vector<int> bestD;
			for (int d = 0; d < 4; ++ d) {
				auto m = move;
				auto a = vector<int>{d, -1, -1, -1, -1, -1};
				m.update(a);
				// 自身のエージェントで塗られているマス数をカウントする
				int c = 0;
				for (int i = 0; i < 6; ++ i) {
					for (int j = 0; j < N; ++ j) {
						for (int k = 0; k < N; ++ k) {
							if (m.field[i][j][k][0] == 0) {
								++ c;
							}
						}
					}
				}
				// 最も多くのマスを自身のエージェントで塗れる移動方向のリストを保持する
				if (c > bestC) {
					bestC = c;
					bestD.clear();
					bestD.push_back(d);
				} else if (c == bestC) {
					bestD.push_back(d);
				}
			}
			// 最も多くのマスを自身のエージェントで塗れる移動方向のリストからランダムで方向を決める
			dir = bestD[uniform_int_distribution<>(0, bestD.size() - 1)(mt)];
		}
	}
};

int main() {
	random_device seed_gen;
	mt = mt19937(seed_gen());

	Bot bot;
	bot.solve();
}
