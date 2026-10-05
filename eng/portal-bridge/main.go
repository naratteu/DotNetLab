//go:build js && wasm

// A Portal connector that hands every HTTP request to a JavaScript function, so a server
// written in another wasm runtime (.NET here) can answer it. src/WebAssembly/wwwroot/portal/bridge.js
// sets globalThis.dotnetlabPortalHandle(request) to return a Promise of {status, headers, body}.
//
// Build with eng/portal-bridge/build.sh.
package main

import (
	"context"
	"errors"
	"io"
	"net/http"
	"syscall/js"

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
	body, err := io.ReadAll(r.Body)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	headers := js.Global().Get("Array").New()
	for name, values := range r.Header {
		for _, v := range values {
			headers.Call("push", []any{name, v})
		}
	}
	jsBody := js.Global().Get("Uint8Array").New(len(body))
	js.CopyBytesToJS(jsBody, body)
	req := js.Global().Get("Object").New()
	req.Set("method", r.Method)
	req.Set("url", r.URL.RequestURI())
	req.Set("host", r.Host)
	req.Set("headers", headers)
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
	out := resp.Get("body")
	if out.Truthy() {
		buf := make([]byte, out.Get("length").Int())
		js.CopyBytesToGo(buf, out)
		_, _ = w.Write(buf)
	}
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
