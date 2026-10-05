//go:build js && wasm

// A Portal connector that hands every HTTP request to a JavaScript function, so a server
// written in another wasm runtime (.NET here) can answer it. src/WebAssembly/wwwroot/portal/bridge.js
// sets globalThis.dotnetlabPortalHandle(request) to return a Promise of {status, headers, body}
// and globalThis.dotnetlabPortalSocket(request, onMessage, onClose) to open a WebSocket.
//
// Build with eng/portal-bridge/build.sh.
package main

import (
	"context"
	"errors"
	"io"
	"net/http"
	"strings"
	"sync"
	"syscall/js"
	"time"

	"github.com/gorilla/websocket"
	"github.com/gosuda/portal-tunnel/v2/portal/identity"
	"github.com/gosuda/portal-tunnel/v2/sdk"
)

func main() {
	js.Global().Set("portalBridge", js.FuncOf(func(_ js.Value, args []js.Value) any {
		relayURL, name, onReady, onError := args[0].String(), args[1].String(), args[2], args[3]
		go func() {
			id, err := identity.Generate(name)
			if err != nil {
				onError.Invoke(err.Error())
				return
			}
			ctx := context.Background()
			exposure, err := sdk.Expose(ctx, id, []string{relayURL})
			if err != nil {
				onError.Invoke(err.Error())
				return
			}
			go sdk.RunHTTP(ctx, exposure, http.HandlerFunc(serve), "")
			ready, err := exposure.WaitReady(ctx)
			if err != nil || len(ready) == 0 || ready[0].PublicURL == "" {
				onError.Invoke("relay did not become ready")
				return
			}
			onReady.Invoke(ready[0].PublicURL)
		}()
		return nil
	}))
	select {}
}

func serve(w http.ResponseWriter, r *http.Request) {
	if strings.EqualFold(r.Header.Get("Upgrade"), "websocket") {
		serveWebSocket(w, r)
		return
	}

	body, err := io.ReadAll(r.Body)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	req := toJS(r)
	jsBody := js.Global().Get("Uint8Array").New(len(body))
	js.CopyBytesToJS(jsBody, body)
	req.Set("body", jsBody)

	resp, err := await(js.Global().Call("dotnetlabPortalHandle", req))
	if err != nil {
		http.Error(w, "handler failed: "+err.Error(), http.StatusBadGateway)
		return
	}
	hs := resp.Get("headers")
	for i := 0; i < hs.Length(); i++ {
		w.Header().Add(hs.Index(i).Index(0).String(), hs.Index(i).Index(1).String())
	}
	w.WriteHeader(resp.Get("status").Int())
	if out := resp.Get("body"); out.Truthy() {
		_, _ = w.Write(fromJS(out))
	}
}

// serveWebSocket opens the socket on the .NET side first, so the handshake can answer
// with the subprotocol the server picked, then pumps messages both ways.
// (coder/websocket, which the SDK uses, cannot accept connections when compiled to WebAssembly.)
func serveWebSocket(w http.ResponseWriter, r *http.Request) {
	req := toJS(r)
	protocols := js.Global().Get("Array").New()
	for _, p := range websocket.Subprotocols(r) {
		protocols.Call("push", p)
	}
	req.Set("protocols", protocols)

	// Callbacks from JavaScript must not block, so messages queue up without a bound.
	type message struct {
		typ   int
		data  []byte
		close bool
	}
	var mu sync.Mutex
	var queue []message
	wake := make(chan struct{}, 1)
	enqueue := func(m message) {
		mu.Lock()
		queue = append(queue, m)
		mu.Unlock()
		select {
		case wake <- struct{}{}:
		default:
		}
	}
	onMessage := js.FuncOf(func(_ js.Value, a []js.Value) any {
		typ := websocket.BinaryMessage
		if a[0].Bool() {
			typ = websocket.TextMessage
		}
		enqueue(message{typ: typ, data: fromJS(a[1])})
		return nil
	})
	onClose := js.FuncOf(func(_ js.Value, a []js.Value) any {
		enqueue(message{close: true, data: websocket.FormatCloseMessage(a[0].Int(), a[1].String())})
		return nil
	})
	defer onMessage.Release()
	defer onClose.Release()

	opened, err := await(js.Global().Call("dotnetlabPortalSocket", req, onMessage, onClose))
	if err != nil {
		http.Error(w, "websocket failed: "+err.Error(), http.StatusBadGateway)
		return
	}
	id := opened.Get("id")
	upgrader := websocket.Upgrader{CheckOrigin: func(*http.Request) bool { return true }}
	if p := opened.Get("protocol"); p.Truthy() {
		upgrader.Subprotocols = []string{p.String()}
	}
	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		js.Global().Call("dotnetlabPortalSocketClose", id, websocket.CloseInternalServerErr, err.Error())
		return
	}
	defer conn.Close()
	done := make(chan struct{})
	defer close(done)

	// Server to browser.
	go func() {
		for {
			select {
			case <-done:
				return
			case <-wake:
			}
			mu.Lock()
			pending := queue
			queue = nil
			mu.Unlock()
			for _, m := range pending {
				if m.close {
					_ = conn.WriteControl(websocket.CloseMessage, m.data, time.Now().Add(5*time.Second))
					_ = conn.Close()
					return
				}
				if conn.WriteMessage(m.typ, m.data) != nil {
					_ = conn.Close()
					return
				}
			}
		}
	}()

	// Browser to server.
	for {
		typ, data, err := conn.ReadMessage()
		if err != nil {
			code := websocket.CloseGoingAway
			var closeErr *websocket.CloseError
			if errors.As(err, &closeErr) {
				code = closeErr.Code
			}
			js.Global().Call("dotnetlabPortalSocketClose", id, code, "")
			return
		}
		jsData := js.Global().Get("Uint8Array").New(len(data))
		js.CopyBytesToJS(jsData, data)
		js.Global().Call("dotnetlabPortalSocketSend", id, typ == websocket.TextMessage, jsData)
	}
}

func toJS(r *http.Request) js.Value {
	headers := js.Global().Get("Array").New()
	for name, values := range r.Header {
		for _, v := range values {
			headers.Call("push", []any{name, v})
		}
	}
	req := js.Global().Get("Object").New()
	req.Set("method", r.Method)
	req.Set("url", r.URL.RequestURI())
	req.Set("host", r.Host)
	req.Set("headers", headers)
	return req
}

func fromJS(v js.Value) []byte {
	buf := make([]byte, v.Get("length").Int())
	js.CopyBytesToGo(buf, v)
	return buf
}

// await blocks this goroutine until the JavaScript promise settles.
func await(p js.Value) (js.Value, error) {
	done := make(chan js.Value, 1)
	fail := make(chan string, 1)
	then := js.FuncOf(func(_ js.Value, a []js.Value) any { done <- a[0]; return nil })
	catch := js.FuncOf(func(_ js.Value, a []js.Value) any { fail <- a[0].Call("toString").String(); return nil })
	defer then.Release()
	defer catch.Release()
	js.Global().Get("Promise").Call("resolve", p).Call("then", then, catch)
	select {
	case v := <-done:
		return v, nil
	case e := <-fail:
		return js.Undefined(), errors.New(e)
	}
}
