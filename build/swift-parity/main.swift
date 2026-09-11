// Generates the Swift-side golden files the C# Store tests compare against.
//
// Compiled together with the Swift app's own STModel and STStore sources (see
// generate.sh), so every byte in the goldens comes from the real JSONEncoder and
// the real ssh_config importer, not from anyone's reading of them.
import Foundation

let arguments = CommandLine.arguments
guard arguments.count == 3 else {
    FileHandle.standardError.write(Data("usage: generate <fixtures dir> <ssh_config sample>\n".utf8))
    exit(2)
}
let fixtures = URL(fileURLWithPath: arguments[1], isDirectory: true)
let sshSample = URL(fileURLWithPath: arguments[2])

/// Deterministic ids, so regenerating does not churn every line of the goldens.
func id(_ n: Int) -> NodeID {
    NodeID(uuidString: String(format: "00000000-0000-4000-8000-%012d", n))!
}

let encoder = JSONEncoder()
encoder.outputFormatting = [.prettyPrinted, .sortedKeys]

// MARK: - 1. An inventory touching every field, saved by the real store

let opsKey = Credential(id: id(101), name: "Ops key", username: "ops",
                        method: .identityFile, identityFile: "/keys/ops_ed25519")
let personalAgent = Credential(id: id(102), name: "Personal agent", method: .agent, sortIndex: 1)
let legacyPassword = Credential(id: id(103), name: "Vault \"legacy\" password", username: "admin",
                                method: .password, sortIndex: 1)

let production = Folder(
    id: id(1), name: "Production",
    settings: ConnectionSettings(
        username: "deploy", port: 22, connectTimeout: 20, keepAliveInterval: 45,
        compression: true, forwardAgent: false, hostKeyPolicy: .strict,
        knownHostsFile: "~/.ssh/known_hosts.prod", credentialID: opsKey.id,
        terminalTheme: "Solarized / Dark", terminalFontSize: 13.5,
        identityFiles: ["/keys/a", "/keys/b"], jumpHosts: ["bastion", "edge"],
        environment: [
            "LANG": "en_US.UTF-8", "TZ": "UTC", "AWS_PROFILE": "prod", "aws_region": "eu-west-1",
            "PATH_2": "two", "PATH_10": "ten", "_PRIVATE": "1", "Zeta": "z", "alpha": "a",
            "A01": "n1", "A1": "n2", "a1": "n3",
        ],
        portForwards: [
            PortForward(id: id(201), kind: .local, name: "postgres", bindPort: 5432,
                        destinationHost: "localhost", destinationPort: 5432, autoStart: true),
            PortForward(id: id(202), kind: .remote, name: "callback", bindAddress: "0.0.0.0",
                        bindPort: 9000, destinationHost: "127.0.0.1", destinationPort: 9000),
            PortForward(id: id(203), kind: .dynamic, name: "SOCKS 1080", bindPort: 1080),
        ]
    ),
    tags: ["prod", "critical", "eu-west"]
)
let databases = Folder(id: id(2), parentID: production.id, name: "Databases")
let personal = Folder(id: id(3), name: "Personal — café ☕", sortIndex: 1)

let connections = [
    Connection(id: id(11), parentID: databases.id, name: "db-01", hostname: "db-01.internal",
               settings: ConnectionSettings(username: "postgres", terminalFontSize: 13),
               tags: ["postgres", "primary"],
               lastConnectedAt: Date(timeIntervalSinceReferenceDate: 779_000_000.123456)),
    Connection(id: id(12), parentID: production.id, name: "web-01", hostname: "web-01.example.com",
               lastConnectedAt: Date(timeIntervalSinceReferenceDate: -12_345.5),
               importedFromSSHConfig: true),
    Connection(id: id(13), parentID: personal.id, name: "home-nas", hostname: "192.168.1.20",
               settings: ConnectionSettings(port: 2222, credentialID: personalAgent.id),
               sortIndex: 1),
]

let snippets = [
    Snippet(id: id(301), name: "tail a log", command: "tail -f {{path}}",
            folderID: production.id, tags: ["logs"]),
    Snippet(id: id(302), name: "disk usage", command: "df -h | grep '/dev/' && echo \"done\"\t# tab"),
    Snippet(id: id(303), name: "escaping", command: "back\\slash \u{1}\u{1F}\u{7F} \u{2028} 😀 <>&'+ \r\n",
            sortIndex: 2),
]

let tree = InventoryTree(
    folders: [production, databases, personal], connections: connections,
    snippets: snippets, credentials: [opsKey, personalAgent, legacyPassword]
)
try InventoryStore(url: fixtures.appendingPathComponent("inventory.swift.json")).save(tree)

// MARK: - 2. Encoder formatting edge cases the inventory cannot reach

struct EncoderProbe: Encodable {
    let doubles: [Double]
    let ints: [Int]
    let strings: [String]
    let keys: [String: Int]
    let emptyArray: [Int]
    let emptyObject: [String: Int]
    let nested: [[String: [Int]]]
}

let probeKeys = [
    "b", "B", "a", "A", "a10", "a9", "a01", "a1", "_x", "Z", "é", "e", "f", "ab", "a-b", "a_b",
    "AB", "Ab", "aB", "ｆ", "1", "10", "9", "", " ", "x2y", "x10y",
]
let probe = EncoderProbe(
    doubles: [
        0, -0.0, 1, 13, 13.5, -2.5, 0.1, 1.0 / 3.0, 0.0001, 0.00001, 123_456.789,
        779_000_000.123456, 1e15, 1e16, 9_007_199_254_740_992, 18_014_398_509_481_984,
        1.5e17, 1e21, 1e22, 1e100, 5e-324, 1.7976931348623157e308, 2.2250738585072014e-308,
        100, 1_234_567,
    ],
    ints: [0, -1, Int.max, Int.min],
    strings: [
        "plain", "slash/inside", "quote\"", "back\\slash", "tab\t", "newline\n", "cr\r",
        "\u{8}\u{c}", "\u{0}\u{1}\u{1F}", "\u{7F}", "café", "😀", "\u{2028}\u{2029}", "<>&'+", "\u{FEFF}",
    ],
    keys: Dictionary(uniqueKeysWithValues: probeKeys.enumerated().map { ($1, $0) }),
    emptyArray: [],
    emptyObject: [:],
    nested: [["inner": [1, 2]], [:], ["empty": []]]
)
try encoder.encode(probe).write(to: fixtures.appendingPathComponent("encoder-probe.swift.json"))

// MARK: - 3. The real importer over a representative ssh_config

let (imported, warnings) = SSHConfigImporter.inventory(
    from: SSHConfigParser.parse(try String(contentsOf: sshSample, encoding: .utf8))
)

// Machine-specific and random parts are normalised identically on the C# side:
// the home directory becomes $HOME, and ids are renumbered in document order.
let home = NSHomeDirectory()
func unhome(_ path: String) -> String {
    path.hasPrefix(home) ? "$HOME" + String(path.dropFirst(home.count)) : path
}
var nextForward = 1000
func normalize(_ settings: ConnectionSettings) -> ConnectionSettings {
    var copy = settings
    copy.identityFiles = settings.identityFiles?.map(unhome)
    copy.knownHostsFile = settings.knownHostsFile.map(unhome)
    copy.portForwards = settings.portForwards?.map { forward in
        nextForward += 1
        return PortForward(id: id(nextForward), kind: forward.kind, name: forward.name,
                           bindAddress: forward.bindAddress, bindPort: forward.bindPort,
                           destinationHost: forward.destinationHost,
                           destinationPort: forward.destinationPort, autoStart: forward.autoStart)
    }
    return copy
}

let importedFolder = imported.folders.values.first!
let folder = Folder(id: id(1), name: importedFolder.name, settings: normalize(importedFolder.settings),
                    tags: importedFolder.tags, sortIndex: importedFolder.sortIndex)
let importedConnections = imported.connections.values.sorted { $0.sortIndex < $1.sortIndex }.map {
    Connection(id: id(10 + $0.sortIndex), parentID: folder.id, name: $0.name, hostname: $0.hostname,
               settings: normalize($0.settings), tags: $0.tags, sortIndex: $0.sortIndex,
               lastConnectedAt: $0.lastConnectedAt, importedFromSSHConfig: $0.importedFromSSHConfig)
}

func describe(_ warning: SSHConfigImporter.Warning) -> String {
    let line = warning.lineNumber.map(String.init) ?? "-"
    switch warning.reason {
    case .matchBlockSkipped(let criteria): return "\(line) matchBlockSkipped \(criteria.joined(separator: " "))"
    case .unsupportedKeyword(let keyword): return "\(line) unsupportedKeyword \(keyword)"
    case .malformedValue(let keyword, let value): return "\(line) malformedValue \(keyword) \(value)"
    }
}

struct ImportGolden: Encodable {
    let document: InventoryDocument
    let warnings: [String]
}
let importGolden = ImportGolden(
    document: InventoryDocument(folders: [folder], connections: importedConnections),
    warnings: warnings.map(describe)
)
try encoder.encode(importGolden).write(to: fixtures.appendingPathComponent("ssh_config.import.swift.json"))
