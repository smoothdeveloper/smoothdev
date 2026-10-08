/// Free-port detection: probe, then pick the first free port at or above the preferred one.
module SmoothDev.Web.Ports

open System.Net
open System.Net.Sockets

let loopbacks =
  [|
    IPAddress.Loopback
    if Socket.OSSupportsIPv6 then
      IPAddress.IPv6Loopback
  |]

/// True when something accepts TCP connections on this address and port.
let isListening (address: IPAddress) port =
  try
    use client = new TcpClient(address.AddressFamily)
    client.ConnectAsync(address, port).Wait 250 && client.Connected
  with _ ->
    false

/// True when this process could bind the port on this address right now.
let canBind address port =
  try
    let listener = new TcpListener(address, port)
    listener.ExclusiveAddressUse <- true
    listener.Start()
    listener.Stop()
    true
  with _ ->
    false

/// Free: nothing answers on 127.0.0.1 or ::1, and 127.0.0.1 can be bound. Connecting catches listeners
/// a bind probe can miss (SO_REUSEADDR, wildcard binds).
let isFree port =
  port > 0
  && port < 65536
  && not (loopbacks |> Array.exists (fun a -> isListening a port))
  && canBind IPAddress.Loopback port

/// A port the OS considers free right now.
let ephemeral () =
  let listener = new TcpListener(IPAddress.Loopback, 0)
  listener.Start()
  let port = (listener.LocalEndpoint :?> IPEndPoint).Port
  listener.Stop()
  port

/// First port in [preferred, preferred + window) that is not reserved and passes isFree; an ephemeral
/// port when the whole window is taken. Never fails because a port is busy.
let pickWith isFree reserved preferred window =
  seq { preferred .. min 65535 (preferred + window - 1) }
  |> Seq.tryFind (fun p -> not (Array.contains p reserved) && isFree p)
  |> Option.defaultWith ephemeral

let pick reserved preferred = pickWith isFree reserved preferred 100
