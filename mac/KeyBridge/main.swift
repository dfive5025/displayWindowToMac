// KeyBridge: receives keystrokes from the Windows receiver and types them on this Mac.
//
// OpenDisplay's protocol carries mouse and touch only, so keyboard input from the
// Windows PC travels on this separate, small connection:
//
//   TCP 9100, frames [4-byte big-endian length][UTF-8 JSON]
//   client -> {"type":"auth","code":"123456","name":"DESKTOP-ABC"}
//   server -> {"type":"authOk"} | {"type":"authFail"} (then closes)
//   client -> {"type":"key","code":<macOS virtual key code>,"down":true|false}
//   client -> {"type":"releaseAll"}            (window lost focus)
//   client -> {"type":"ping"}                  (every 2 s; idle links close after 10 s)
//
// Anyone on the network could type on this Mac, so a 6-digit pairing code is
// required. It is printed when KeyBridge starts and saved in
// ~/Library/Application Support/KeyBridge/code.
//
// Build:  swiftc -O mac/KeyBridge/main.swift -o KeyBridge
// Run:    ./KeyBridge          (grant Accessibility to the app that runs it, e.g. Terminal)

import ApplicationServices
import Foundation
import Network

let port: UInt16 = 9100
let serviceType = "_odkeys._tcp"

// MARK: - Pairing code

func pairingCode() -> String {
    let dir = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/KeyBridge", isDirectory: true)
    let file = dir.appendingPathComponent("code")
    if let saved = try? String(contentsOf: file, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines),
       saved.count == 6, saved.allSatisfy(\.isNumber) {
        return saved
    }
    let fresh = String(format: "%06d", Int.random(in: 0...999_999))
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    try? fresh.write(to: file, atomically: true, encoding: .utf8)
    return fresh
}

let code = pairingCode()

// MARK: - Key injection

/// Synthesizes key events. Modifier state is tracked here and stamped on every
/// event, because a synthetic key-down does not pick up flags from earlier
/// synthetic modifier presses on its own.
final class Injector {
    private let source = CGEventSource(stateID: .hidSystemState)
    private var flags: CGEventFlags = []
    private var down = Set<UInt16>()

    private static let modifierFlags: [UInt16: CGEventFlags] = [
        0x37: .maskCommand, 0x36: .maskCommand,     // left / right command
        0x38: .maskShift, 0x3C: .maskShift,         // left / right shift
        0x3A: .maskAlternate, 0x3D: .maskAlternate, // left / right option
        0x3B: .maskControl, 0x3E: .maskControl,     // left / right control
        0x3F: .maskSecondaryFn,                     // fn
    ]

    func key(_ code: UInt16, isDown: Bool) {
        if let flag = Self.modifierFlags[code] {
            if isDown { down.insert(code) } else { down.remove(code) }
            // A flag stays set while either side's key of that modifier is held.
            let held = Self.modifierFlags.filter { down.contains($0.key) }.map(\.value)
            flags = held.reduce(into: CGEventFlags()) { $0.formUnion($1) }
            guard let event = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: isDown) else { return }
            event.type = .flagsChanged
            event.flags = flags
            event.post(tap: .cghidEventTap)
            _ = flag
            return
        }
        if isDown { down.insert(code) } else { down.remove(code) }
        guard let event = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: isDown) else { return }
        event.flags = flags
        event.post(tap: .cghidEventTap)
    }

    func releaseAll() {
        for code in down { key(code, isDown: false) }
        down.removeAll()
        flags = []
    }
}

let injector = Injector()

// MARK: - Connections

func frame(_ json: String) -> Data {
    let payload = Data(json.utf8)
    var length = UInt32(payload.count).bigEndian
    var data = Data(bytes: &length, count: 4)
    data.append(payload)
    return data
}

final class Session {
    private let conn: NWConnection
    private var authed = false
    private var lastRx = Date()
    private var timer: DispatchSourceTimer?
    private let name: String
    var onStop: (() -> Void)?

    init(_ conn: NWConnection) {
        self.conn = conn
        self.name = "\(conn.endpoint)"
    }

    func start() {
        conn.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed, .cancelled: self?.stop()
            default: break
            }
        }
        conn.start(queue: .main)
        let t = DispatchSource.makeTimerSource(queue: .main)
        t.schedule(deadline: .now() + 2, repeating: 2)
        t.setEventHandler { [weak self] in
            guard let self else { return }
            if Date().timeIntervalSince(self.lastRx) > 10 { self.stop() }
        }
        t.resume()
        timer = t
        readHeader()
    }

    private func stop() {
        guard timer != nil else { return }
        timer?.cancel()
        timer = nil
        if authed {
            injector.releaseAll()
            print("disconnected: \(name)")
        }
        conn.cancel()
        onStop?()
        onStop = nil
    }

    private func readHeader() {
        conn.receive(minimumIncompleteLength: 4, maximumLength: 4) { [weak self] data, _, _, error in
            guard let self, let data, data.count == 4, error == nil else { self?.stop(); return }
            let length = data.withUnsafeBytes { Int(UInt32(bigEndian: $0.loadUnaligned(as: UInt32.self))) }
            guard length > 0, length < 64 * 1024 else { self.stop(); return }
            self.conn.receive(minimumIncompleteLength: length, maximumLength: length) { [weak self] body, _, _, error in
                guard let self, let body, body.count == length, error == nil else { self?.stop(); return }
                self.lastRx = Date()
                self.handle(body)
                self.readHeader()
            }
        }
    }

    private func handle(_ body: Data) {
        guard let obj = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any],
              let type = obj["type"] as? String else { return }
        if !authed {
            guard type == "auth" else { stop(); return }
            if (obj["code"] as? String) == code {
                authed = true
                conn.send(content: frame("{\"type\":\"authOk\"}"), completion: .contentProcessed { _ in })
                print("connected: \(obj["name"] as? String ?? name)")
            } else {
                print("rejected \(name): wrong pairing code")
                conn.send(content: frame("{\"type\":\"authFail\"}"), completion: .contentProcessed { [weak self] _ in
                    self?.stop()
                })
            }
            return
        }
        switch type {
        case "key":
            if let c = obj["code"] as? Int, c >= 0, c < 0x80, let isDown = obj["down"] as? Bool {
                injector.key(UInt16(c), isDown: isDown)
            }
        case "releaseAll":
            injector.releaseAll()
        default:
            break // ping and anything newer
        }
    }
}

// MARK: - Main

let trusted = AXIsProcessTrustedWithOptions(
    [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)

print("KeyBridge listening on TCP \(port)")
print("Pairing code: \(code)   (type it into the Windows app when it asks)")
if !trusted {
    print("Accessibility permission is missing: System Settings > Privacy & Security > Accessibility,")
    print("turn on the app that runs KeyBridge (e.g. Terminal), then start KeyBridge again.")
}

var sessions: [ObjectIdentifier: Session] = [:]
let params = NWParameters.tcp
params.allowLocalEndpointReuse = true
(params.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options)?.noDelay = true
let listener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: port)!)
listener.service = NWListener.Service(name: Host.current().localizedName ?? "Mac", type: serviceType)
listener.newConnectionHandler = { conn in
    let session = Session(conn)
    let key = ObjectIdentifier(session)
    sessions[key] = session
    session.onStop = { sessions[key] = nil }
    session.start()
}
listener.stateUpdateHandler = { state in
    if case .failed(let error) = state {
        print("listener failed: \(error)")
        exit(1)
    }
}
listener.start(queue: .main)
dispatchMain()
