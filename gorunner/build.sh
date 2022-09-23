#!/bin/bash

GOOS=windows GOARCH=amd64 go build "-ldflags=-s -w" -o gorunner-win-x64.exe main.go
GOOS=linux GOARCH=amd64   go build "-ldflags=-s -w" -o gorunner-linux-x64   main.go
GOOS=darwin GOARCH=amd64  go build "-ldflags=-s -w" -o gorunner-darwin-x64  main.go
