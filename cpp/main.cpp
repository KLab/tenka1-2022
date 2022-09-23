#include <cstdlib>
#include <cstring>
#include <iostream>
#include <vector>
#include <thread>
#include <chrono>
#include <random>
#include <curl/curl.h>
#include <rapidjson/document.h>

using namespace std;

mt19937 mt;

const char* GameServer;
const char* TOKEN;

const int N = 5;
const int Dj[] {+1, 0, -1, 0};
const int Dk[] {0, +1, 0, -1};

struct memory {
	char *response;
	size_t size;
};

static size_t curl_cb(void *data, size_t size, size_t nmemb, void *userp)
{
	size_t realsize = size * nmemb;
	memory *mem = (memory*)userp;

	char *ptr = (char*)realloc(mem->response, mem->size + realsize + 1);
	if (ptr == nullptr) return 0;

	mem->response = ptr;
	memcpy(&(mem->response[mem->size]), data, realsize);
	mem->size += realsize;
	mem->response[mem->size] = 0;

	return realsize;
}

// ゲームサーバのAPIを叩く
rapidjson::Document call_api(string x) {
	auto url = GameServer + x;
	for (int i = 0; i < 5; ++ i) {
		cout << url << endl;
		CURL *curl = curl_easy_init();
		if (curl == nullptr) {
			cerr << "curl_easy_init failure" << endl;
			throw 1;
		}
		memory chunk = {0};
		curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
		curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, curl_cb);
		curl_easy_setopt(curl, CURLOPT_WRITEDATA, (void *)&chunk);
		auto res = curl_easy_perform(curl);
		if (res != CURLE_OK) {
			cerr << "curl error " << res << endl;
			this_thread::sleep_for(100ms);
			continue;
		}
		long response_code;
		curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &response_code);
		curl_easy_cleanup(curl);
		if (response_code != 200) {
			cerr << "http error " << response_code << endl;
			if (response_code >= 500) {
				this_thread::sleep_for(100ms);
				continue;
			}
			throw 1;
		}
		rapidjson::Document doc;
		doc.Parse(chunk.response);
		return doc;
	}
	throw 1;
}

// game_idを取得する
// 環境変数で指定されていない場合は練習試合のgame_idを返す
int get_game_id() {
	// 環境変数にGAME_IDが設定されている場合これを優先する
	auto p = getenv("GAME_ID");
	if (p != nullptr) {
		return atoi(p);
	}

	// start APIを呼び出し練習試合のgame_idを取得する
	int mode = 0;
	int delay = 0;

	auto start = call_api("/api/start/" + string(TOKEN) + "/" + to_string(mode) + "/" + to_string(delay));
	auto status = start["status"].GetString();
	if (strcmp(status, "ok") == 0 || strcmp(status, "started") == 0) {
		return start["game_id"].GetInt();
	}

	throw 1;
}

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
	void call_move(int game_id, int dir) {
		auto res = call_api("/api/move/" + string(TOKEN) + "/" + to_string(game_id) + "/" + to_string(dir));
		status = res["status"].GetString();
		if (status != "ok") return;
		now = res["now"].GetInt64();
		turn = res["turn"].GetInt();
		for (auto it = res["move"].Begin(); it != res["move"].End(); ++ it) {
			move.push_back(it->GetInt());
		}
		for (auto it = res["score"].Begin(); it != res["score"].End(); ++ it) {
			score.push_back(it->GetInt());
		}
		for (auto it1 = res["field"].Begin(); it1 != res["field"].End(); ++ it1) {
			field.push_back(vector<vector<vector<int>>>());
			for (auto it2 = it1->Begin(); it2 != it1->End(); ++ it2) {
				field.back().push_back(vector<vector<int>>());
				for (auto it3 = it2->Begin(); it3 != it2->End(); ++ it3) {
					field.back().back().push_back(vector<int>());
					for (auto it4 = it3->Begin(); it4 != it3->End(); ++ it4) {
						field.back().back().back().push_back(it4->GetInt());
					}
				}
			}
		}
		for (auto it1 = res["agent"].Begin(); it1 != res["agent"].End(); ++ it1) {
			agent.push_back(vector<int>());
			for (auto it2 = it1->Begin(); it2 != it1->End(); ++ it2) {
				agent.back().push_back(it2->GetInt());
			}
		}
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
			if (a[idx] == -1) {
				continue;
			}
			rotate_agent(idx, a[idx]);
			move_forward(idx);
		}

		// フィールドの更新処理
		for (int idx = 0; idx < 6; ++ idx) {
			if (a[idx] == -1) {
				continue;
			}
			bool ok = true;
			for (int j = 0; j < 6; ++ j) {
				if (idx == j || a[j] == -1 || !is_same_pos(agent[idx], agent[j]) || is_owned_cell(idx)) {
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
		auto game_id = get_game_id();

		auto dir = uniform_int_distribution<>(0, 3)(mt);
		for (;;) {
			// 移動APIを呼ぶ
			Move move;
			move.call_move(game_id, dir);
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

	GameServer = getenv("GAME_SERVER");
	if (GameServer == nullptr) GameServer = "https://2022contest.gbc.tenka1.klab.jp";
	TOKEN = getenv("TOKEN");
	if (TOKEN == nullptr) TOKEN = "YOUR_TOKEN";

	Bot bot;
	bot.solve();
}
