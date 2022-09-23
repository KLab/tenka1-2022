package main

import (
	"bufio"
	"context"
	_ "embed"
	"encoding/json"
	"errors"
	"fmt"
	"html/template"
	"io"
	"io/ioutil"
	"log"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/BurntSushi/toml"
	"github.com/pkg/browser"
)

//go:embed index.html
var indexHtml string

const (
	DefaultGameServer = "https://2022contest.gbc.tenka1.klab.jp"
	MaxOutputFiles    = 50
)

type OutputFile struct {
	GameId   int
	Cmd      string
	ExitCode int
}

type Config struct {
	ListenPort      int    `toml:"listen_port"`
	GameServer      string `toml:"game_server"`
	Token           string `toml:"token"`
	Pwd             string `toml:"pwd"`
	PracticeMode    int    `toml:"practice_mode"`
	PracticeDelay   int    `toml:"practice_delay"`
	PracticeCommand string `toml:"practice_command"`
}

type ExecutingProcess struct {
	Pid      int
	Cmd      string
	GameId   int
	GameType string
}

var (
	conf               Config
	outputDir          string
	configFilePath     string
	logFilePath        string
	reloadTemplate     = false
	indexTemplate      *template.Template
	gMtx               sync.Mutex
	lastErrorMsg       string
	lastErrorTime      time.Time
	commands           []string
	outputFiles        []*OutputFile
	executingProcesses []ExecutingProcess
)

// バイナリから起動しているかどうか
func IsExecuteFromBinary() bool {
	executable, err := os.Executable()
	if err != nil {
		return false
	}

	goTmpDir := os.Getenv("GOTMPDIR")
	if "" != goTmpDir {
		return !strings.HasPrefix(executable, goTmpDir)
	}

	return !strings.HasPrefix(executable, os.TempDir())
}

func init() {
	indexTemplate = template.Must(template.New("index.html").Parse(indexHtml))
	commands = []string{"", "", "", ""}

	if IsExecuteFromBinary() {
		execPath, err := os.Executable()
		if err != nil {
			log.Fatalf("os.Executable: %s", err)
		}
		currentDir := filepath.Dir(execPath)
		err = os.Chdir(currentDir)
		if err != nil {
			log.Fatalf("os.Chdir: %s", err)
		}
	}

	if path, err := filepath.Abs("output"); err != nil {
		log.Fatalf("filepath.Abs: %s", err)
	} else {
		outputDir = path
	}

	if path, err := filepath.Abs("config.toml"); err != nil {
		log.Fatalf("filepath.Abs: %s", err)
	} else {
		configFilePath = path
	}

	if path, err := filepath.Abs("gorunner.txt"); err != nil {
		log.Fatalf("filepath.Abs: %s", err)
	} else {
		logFilePath = path
	}

	_ = loadConfig()
	if os.Getenv("GAME_SERVER") != "" {
		conf.GameServer = os.Getenv("GAME_SERVER")
	}
	if os.Getenv("TOKEN") != "" {
		conf.Token = os.Getenv("TOKEN")
	}
	if os.Getenv("OUTPUT_DIR") != "" {
		outputDir = os.Getenv("OUTPUT_DIR")
	}
	if conf.ListenPort == 0 {
		conf.ListenPort = 8080
	}
	if conf.GameServer == "" {
		conf.GameServer = DefaultGameServer
	}
	if conf.Pwd != "" {
		if err := os.Chdir(conf.Pwd); err != nil {
			conf.Pwd = ""
		}
	}
	if conf.PracticeCommand == "" {
		conf.PracticeCommand = "go run main.go"
	}
	if err := saveConfig(); err != nil {
		log.Fatalf("saveConfig: %s", err)
	}
}

func loadConfig() error {
	_, err := toml.DecodeFile(configFilePath, &conf)
	return err
}

func saveConfig() error {
	f, err := os.OpenFile(configFilePath, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0755)
	if err != nil {
		return err
	}
	defer f.Close()
	return toml.NewEncoder(f).Encode(conf)
}

type Start struct {
	Status string `json:"status"`
	Start  int64  `json:"start"`
	GameId int64  `json:"game_id"`
}

type Join struct {
	Status  string  `json:"status"`
	GameIds []int64 `json:"game_ids"`
}

func callAPI(x string) ([]byte, error) {
	url := conf.GameServer + x
	log.Println(url)
	resp, err := http.Get(url)
	if err != nil {
		return nil, err
	}
	//goland:noinspection GoUnhandledErrorResult
	defer resp.Body.Close()
	body, err := ioutil.ReadAll(resp.Body)
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf(resp.Status)
	}
	return body, err
}

func callStart(mode, delay int) (*Start, error) {
	res, err := callAPI(fmt.Sprintf("/api/start/%s/%d/%d", conf.Token, mode, delay))
	if err != nil {
		return nil, err
	}
	var move Start
	err = json.Unmarshal(res, &move)
	return &move, err
}

func callJoin() (*Join, error) {
	res, err := callAPI(fmt.Sprintf("/api/join/%s", conf.Token))
	if err != nil {
		return nil, err
	}
	var join Join
	err = json.Unmarshal(res, &join)
	return &join, err
}

func writeLine(mtx *sync.Mutex, f *os.File, prefix string, line []byte) error {
	mtx.Lock()
	defer mtx.Unlock()
	if _, err := f.WriteString(prefix); err != nil {
		return err
	}
	if _, err := f.Write(line); err != nil {
		return err
	}
	if _, err := f.WriteString("\n"); err != nil {
		return err
	}
	return nil
}

func removeProcess(beforeProcesses []ExecutingProcess, pid int) []ExecutingProcess {
	processes := []ExecutingProcess{}
	for _, v := range beforeProcesses {
		if v.Pid != pid {
			processes = append(processes, v)
		}
	}
	return processes
}

func execCommand(gameType string, gameId, name string, arg ...string) error {
	// タイムアウトを設定する(1試合2分半なので3分で設定する)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
	defer cancel()
	cmd := exec.CommandContext(ctx, name, arg...)
	cmd.Env = append(os.Environ(), fmt.Sprintf("GAME_SERVER=%s", conf.GameServer), fmt.Sprintf("TOKEN=%s", conf.Token), fmt.Sprintf("GAME_ID=%s", gameId))
	err := func() error {
		stdoutReader, err := cmd.StdoutPipe()
		if err != nil {
			return errors.New(fmt.Sprintf("execCommand StdoutPipe Error: %v", err))
		}
		stderrReader, err := cmd.StderrPipe()
		if err != nil {
			return errors.New(fmt.Sprintf("execCommand StderrPipe Error: %v", err))
		}
		f, err := os.OpenFile(path.Join(outputDir, gameId+".txt"), os.O_WRONLY|os.O_CREATE|os.O_APPEND, 0644)
		if err != nil {
			return errors.New(fmt.Sprintf("execCommand OpenFile Error: %v", err))
		}
		ch := make(chan error)
		mtx := new(sync.Mutex)
		cmdStr := name + " " + strings.Join(arg, " ")
		if err := writeLine(mtx, f, "", []byte(cmdStr)); err != nil {
			return errors.New(fmt.Sprintf("[writeLine error] %v", err))
		}

		go func() {
			ch <- func() error {
				r := bufio.NewReader(stdoutReader)
				for {
					line, _, err := r.ReadLine()
					if err == io.EOF {
						return nil
					} else if err != nil {
						return errors.New(fmt.Sprintf("[stdoutReader ReadLine error] %v", err))
					}
					if err := writeLine(mtx, f, "> ", line); err != nil {
						return errors.New(fmt.Sprintf("[stdoutReader writeLine error] %v", err))
					}
				}
			}()
		}()
		go func() {
			ch <- func() error {
				r := bufio.NewReader(stderrReader)
				for {
					line, _, err := r.ReadLine()
					if err == io.EOF {
						return nil
					} else if err != nil {
						return errors.New(fmt.Sprintf("[stderrReader ReadLine error] %v", err))
					}
					if err := writeLine(mtx, f, "# ", line); err != nil {
						return errors.New(fmt.Sprintf("[stderrReader writeLine error] %v", err))
					}
				}
			}()
		}()
		err = cmd.Start()
		if err != nil {
			return errors.New(fmt.Sprintf("execCommand Start Error: %v", err))
		}

		gameIdInt, _ := strconv.Atoi(gameId)
		outputFile := &OutputFile{
			GameId:   gameIdInt,
			Cmd:      cmdStr,
			ExitCode: -99,
		}

		gMtx.Lock()
		outputFiles = append(outputFiles, outputFile)
		sort.Slice(outputFiles, func(i, j int) bool {
			return outputFiles[i].GameId > outputFiles[j].GameId
		})
		executingProcesses = append(executingProcesses, ExecutingProcess{
			Pid:      cmd.Process.Pid,
			Cmd:      cmdStr,
			GameId:   gameIdInt,
			GameType: gameType,
		})
		sort.Slice(executingProcesses, func(i, j int) bool {
			return executingProcesses[i].GameId > executingProcesses[j].GameId
		})
		gMtx.Unlock()

		err1 := <-ch
		err2 := <-ch
		if err1 != nil {
			if err2 != nil {
				return errors.New(fmt.Sprintf("%v %v", err1, err2))
			} else {
				return err1
			}
		} else if err2 != nil {
			return err2
		}

		err = cmd.Wait()
		exitCode := cmd.ProcessState.ExitCode()
		outputFile.ExitCode = exitCode
		if err := writeLine(mtx, f, "ExitCode:", []byte(strconv.Itoa(exitCode))); err != nil {
			return errors.New(fmt.Sprintf("[writeLine error] %v", err))
		}

		err0 := f.Close()
		if err0 != nil {
			return errors.New(fmt.Sprintf("execCommand file Close Error: %v", err))
		}

		gMtx.Lock()
		executingProcesses = removeProcess(executingProcesses, cmd.Process.Pid)
		gMtx.Unlock()

		if err != nil {
			return errors.New(fmt.Sprintf("execCommand Wait Error: %v", err))
		}
		return nil
	}()
	return err
}

func getCommands() []string {
	gMtx.Lock()
	defer gMtx.Unlock()
	r := make([]string, len(commands))
	copy(r, commands)
	return r
}

func setCommands(i int, s string) {
	gMtx.Lock()
	defer gMtx.Unlock()
	commands[i] = s
}

func setLastError(text string) {
	gMtx.Lock()
	defer gMtx.Unlock()
	if text != "" {
		lastErrorMsg = text
		lastErrorTime = time.Now()
		log.Println(text)
	}
}

func getLastError() string {
	gMtx.Lock()
	defer gMtx.Unlock()
	if lastErrorMsg != "" && 30 < time.Since(lastErrorTime).Seconds() {
		lastErrorMsg = ""
	}
	return lastErrorMsg
}

func handleIndex(w http.ResponseWriter, r *http.Request) {
	pwd, err := os.Getwd()
	if err != nil {
		pwd = err.Error()
	}

	if reloadTemplate {
		indexTemplate = template.Must(template.ParseFiles("index.html"))
	}

	commandsTmp := getCommands()
	gMtx.Lock()
	outputFilesTmp := append([]*OutputFile{}, outputFiles...)
	executingProcessesTmp := append([]ExecutingProcess{}, executingProcesses...)
	gMtx.Unlock()

	if MaxOutputFiles < len(outputFilesTmp) {
		outputFilesTmp = outputFilesTmp[:MaxOutputFiles]
	}

	_ = indexTemplate.Execute(w, map[string]interface{}{
		"conf":               conf,
		"outputDir":          outputDir,
		"pwd":                pwd,
		"commands":           commandsTmp,
		"errorMsg":           getLastError(),
		"outputFiles":        outputFilesTmp,
		"executingProcesses": executingProcessesTmp,
	})
}

func handleSetServer(w http.ResponseWriter, r *http.Request) {
	defer http.Redirect(w, r, "/", http.StatusFound)
	if err := r.ParseForm(); err != nil {
		setLastError(fmt.Sprintf("r.ParseForm error: %s", err))
		return
	}

	conf.GameServer = r.Form.Get("server")
	_ = saveConfig()
}

func handleCd(w http.ResponseWriter, r *http.Request) {
	defer http.Redirect(w, r, "/", http.StatusFound)
	if err := r.ParseForm(); err != nil {
		setLastError(fmt.Sprintf("r.ParseForm error: %s", err))
		return
	}

	pwd := r.Form.Get("pwd")
	if err := os.Chdir(pwd); err != nil {
		setLastError(fmt.Sprintf("os.Chdir error: %s", err))
		return
	}
	conf.Pwd = pwd
	_ = saveConfig()
}

func handleSetToken(w http.ResponseWriter, r *http.Request) {
	defer http.Redirect(w, r, "./", http.StatusFound)
	if err := r.ParseForm(); err != nil {
		setLastError(fmt.Sprintf("r.ParseForm error: %s", err))
		return
	}

	conf.Token = r.Form.Get("token")
	_ = saveConfig()
}

func handleStart(w http.ResponseWriter, r *http.Request) {
	defer http.Redirect(w, r, "/", http.StatusFound)
	if err := r.ParseForm(); err != nil {
		setLastError(fmt.Sprintf("r.ParseForm error: %s", err))
		return
	}

	mode, err := strconv.Atoi(r.PostForm["mode"][0])
	if err != nil {
		setLastError(fmt.Sprintf("Atoi(mode) error: %s", err))
		return
	}
	delay, err := strconv.Atoi(r.PostForm["delay"][0])
	if err != nil {
		setLastError(fmt.Sprintf("Atoi(mode) error: %s", err))
		return
	}
	cmd := strings.Split(r.PostForm["command"][0], " ")
	go func() {
		log.Printf("mode:%d delay:%d command:%v", mode, delay, cmd)
		start, err := callStart(mode, delay)
		if err != nil {
			setLastError(fmt.Sprintf("callStart Error: %v", err))
			return
		}
		if start.Status == "ok" || start.Status == "started" {
			log.Printf("start.Start: %d", start.Start)
			log.Printf("start.GameId: %d", start.GameId)
			err = execCommand("練習", fmt.Sprintf("%d", start.GameId), cmd[0], cmd[1:]...)
			if err != nil {
				setLastError(fmt.Sprintf("execCommand Error: %v", err))
				return
			}
		} else {
			setLastError(fmt.Sprintf("start.Status: %s", start.Status))
		}
	}()

	conf.PracticeMode = mode
	conf.PracticeDelay = delay
	conf.PracticeCommand = r.PostForm["command"][0]
	_ = saveConfig()
}

func handleRegister(w http.ResponseWriter, r *http.Request) {
	defer http.Redirect(w, r, "/", http.StatusFound)
	if err := r.ParseForm(); err != nil {
		setLastError(fmt.Sprintf("r.ParseForm error: %s", err))
		return
	}

	command := r.PostForm["registerCommand"][0]
	if len(r.PostForm["agent0"]) == 1 {
		setCommands(0, command)
	}
	if len(r.PostForm["agent1"]) == 1 {
		setCommands(1, command)
	}
	if len(r.PostForm["agent2"]) == 1 {
		setCommands(2, command)
	}
	if len(r.PostForm["agent3"]) == 1 {
		setCommands(3, command)
	}
}

func handleReadLog(w http.ResponseWriter, r *http.Request) {
	gameId := r.URL.Query().Get("id")
	f, err := os.Open(filepath.Join(outputDir, gameId+".txt"))
	if err != nil {
		w.Write([]byte(fmt.Sprintf("os.Open error: %s", err)))
		return
	}
	_, _ = io.Copy(w, f)
}

func runBot(gameId int64, command string) {
	log.Printf("gameId = %d ; command = %s", gameId, command)
	cmd := strings.Split(command, " ")
	err := execCommand("マッチング", fmt.Sprintf("%d", gameId), cmd[0], cmd[1:]...)
	if err != nil {
		setLastError(fmt.Sprintf("runBot: %v", err))
		return
	}
}

func join() {
	gameIds := map[int64]bool{}
	for {
		commands := make([]string, 0, 4)
		for _, v := range getCommands() {
			if v != "" {
				commands = append(commands, v)
			}
		}
		if len(commands) > 0 {
			join, err := callJoin()
			if err != nil {
				setLastError(fmt.Sprintf("callJoin error: %s", err))
			} else if join.Status != "ok" {
				setLastError(fmt.Sprintf("callJoin Status is not ok: %s", join.Status))
			} else {
				i := 0
				for _, gameId := range join.GameIds {
					if _, ok := gameIds[gameId]; !ok {
						gameIds[gameId] = true
						go runBot(gameId, commands[i%len(commands)])
						i++
					}
				}
			}
		}
		time.Sleep(time.Second)
	}
}

func readFirstLine(name string) string {
	fp, err := os.Open(filepath.Join(outputDir, name))
	if err != nil {
		log.Println("os.Open: ", err)
	}
	defer fp.Close()
	scanner := bufio.NewScanner(fp)
	for scanner.Scan() {
		return scanner.Text()
	}
	return ""
}

func readExitCode(name string) int {
	fp, err := os.Open(filepath.Join(outputDir, name))
	if err != nil {
		log.Println("os.Open: ", err)
	}
	defer fp.Close()

	fp.Seek(-20, 2)
	line := ""
	scanner := bufio.NewScanner(fp)
	for scanner.Scan() {
		line = scanner.Text()
	}
	if !strings.HasPrefix(line, "ExitCode:") {
		return -99
	}
	exitCode, _ := strconv.Atoi(line[9:])
	return exitCode
}

func main() {
	if f, err := os.OpenFile(logFilePath, os.O_RDWR|os.O_CREATE|os.O_APPEND, 0666); err == nil {
		defer f.Close()
		log.SetOutput(io.MultiWriter(os.Stdout, f))
	}

	err := os.MkdirAll(outputDir, 0755)
	if err != nil {
		log.Fatalf("os.MkdirAll: %s", err)
	}

	stat, err := os.Stat(outputDir)
	if err != nil {
		log.Fatalf("os.Stat: %s", err)
	}

	if !stat.IsDir() {
		log.Fatalf("%s: not directory", outputDir)
	}

	files, err := ioutil.ReadDir(outputDir)
	if err != nil {
		log.Fatalf("ioutil.ReadDir: %s", err)
	}

	var logGameIds []int
	for _, file := range files {
		if !file.IsDir() && strings.HasSuffix(file.Name(), ".txt") {
			gameId, err := strconv.Atoi(file.Name()[:len(file.Name())-4])
			if err != nil {
				continue
			}
			logGameIds = append(logGameIds, gameId)
		}
	}
	sort.Slice(logGameIds, func(i, j int) bool { return logGameIds[i] > logGameIds[j] })
	if MaxOutputFiles < len(logGameIds) {
		logGameIds = logGameIds[:MaxOutputFiles]
	}

	gMtx.Lock()
	outputFiles = nil
	for _, gameId := range logGameIds {
		name := fmt.Sprintf("%d.txt", gameId)
		outputFiles = append(outputFiles, &OutputFile{
			GameId:   gameId,
			Cmd:      readFirstLine(name),
			ExitCode: readExitCode(name),
		})
	}
	gMtx.Unlock()

	http.HandleFunc("/", handleIndex)
	http.HandleFunc("/setServer", handleSetServer)
	http.HandleFunc("/cd", handleCd)
	http.HandleFunc("/setToken", handleSetToken)
	http.HandleFunc("/start", handleStart)
	http.HandleFunc("/register", handleRegister)
	http.HandleFunc("/readLog", handleReadLog)

	browser.OpenURL(fmt.Sprint("http://localhost:", conf.ListenPort))
	ln, err := net.Listen("tcp", fmt.Sprint(":", conf.ListenPort))
	if err != nil {
		log.Fatalln(err)
	}
	go join()
	log.Fatal(http.Serve(ln, nil))
}
