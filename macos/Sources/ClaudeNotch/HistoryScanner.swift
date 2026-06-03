import Foundation

/// 扫描 ~/.claude/projects/**/*.jsonl（含 subagents/**），把 token/花费按**本地日**聚合成历史。
/// 增量：按 (path, mtime, size) 缓存每文件的「每天贡献」到 supportDir/usage-history.json，
/// 未变文件直接复用、不重读。同步实现，约定在后台串行队列调用。
enum HistoryScanner {

    static var cacheFile: URL { StatuslineHook.supportDir.appendingPathComponent("usage-history.json") }
    static var projectsDir: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude/projects", isDirectory: true)
    }

    struct FileMeta { let url: URL; let mtime: Date; let size: Int }

    /// 全量 + 增量构建。`progress` 回调传 0...1（在调用线程同步触发，外层负责切主线程）。
    static func build(progress: (Double) -> Void) -> UsageHistory {
        var cache = loadCache()
        let files = allTranscriptFiles()
        let formatter = makeFormatter()
        var fresh: [String: FileContribution] = [:]
        var parsedCount = 0, hitCount = 0

        let total = max(1, files.count)
        for (i, f) in files.enumerated() {
            let key = fileKey(f.url, f.mtime, f.size)
            if let cached = cache.files[key] {
                fresh[key] = cached; hitCount += 1
            } else {
                fresh[key] = contribution(of: f.url, formatter: formatter); parsedCount += 1
            }
            if i % 8 == 0 || i == files.count - 1 { progress(Double(i + 1) / Double(total)) }
        }

        // 只保留当前存在文件的贡献 → 自动剔除已删除文件
        cache.files = fresh
        cache.version = HistoryCache.currentVersion
        saveCache(cache)

        var history = UsageHistory()
        for (_, contrib) in fresh {
            for (day, stat) in contrib.days {
                history.days[day, default: DayStat()].merge(stat)
            }
        }
        history.lastBuiltAt = Date()

        if ProcessInfo.processInfo.environment["CLAUDENOTCH_DEBUG"] != nil {
            NSLog("[ClaudeNotch] history build: %d files (%d parsed, %d cached), %d days",
                  files.count, parsedCount, hitCount, history.days.count)
        }
        return history
    }

    // MARK: - 文件枚举

    static func allTranscriptFiles() -> [FileMeta] {
        let fm = FileManager.default
        guard let en = fm.enumerator(at: projectsDir,
                                     includingPropertiesForKeys: [.isRegularFileKey, .contentModificationDateKey, .fileSizeKey],
                                     options: [.skipsHiddenFiles]) else { return [] }
        var out: [FileMeta] = []
        for case let url as URL in en where url.pathExtension == "jsonl" {
            let v = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
            out.append(FileMeta(url: url, mtime: v?.contentModificationDate ?? .distantPast, size: v?.fileSize ?? 0))
        }
        return out
    }

    static func fileKey(_ url: URL, _ mtime: Date, _ size: Int) -> String {
        // 用完整精度 mtime（不取整到秒），避免同一秒内同字节大小的就地重写被误判为未变。
        "\(url.path)|\(mtime.timeIntervalSince1970)|\(size)"
    }

    // MARK: - 单文件 → 每天贡献

    static func contribution(of url: URL, formatter: ISO8601DateFormatter) -> FileContribution {
        guard let content = try? String(contentsOf: url, encoding: .utf8) else { return FileContribution(days: [:]) }
        var days: [LocalDay: DayStat] = [:]
        var seen = Set<String>()
        let cal = Calendar.current
        content.enumerateLines { line, _ in
            guard let p = parseAssistantUsageLine(line) else { return }
            // 文件内按 messageId 去重（同一响应被写多行）；空 id 不折叠，各计一条
            if !p.messageId.isEmpty {
                guard seen.insert(p.messageId).inserted else { return }
            }
            guard let raw = p.timestampRaw, let date = parseISO(raw, formatter) else { return }
            let day = DayKey.from(date, cal)
            let hour = cal.component(.hour, from: date)
            let project = p.cwd.isEmpty ? "(unknown)" : (p.cwd as NSString).lastPathComponent
            days[day, default: DayStat()].add(p.tokens, model: p.model, project: project, hour: hour)
        }
        return FileContribution(days: days)
    }

    // MARK: - 时间戳解析（复用 formatter，量大务必缓存）

    static func makeFormatter() -> ISO8601DateFormatter {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }
    private static let plainFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter(); f.formatOptions = [.withInternetDateTime]; return f
    }()
    static func parseISO(_ s: String, _ f: ISO8601DateFormatter) -> Date? {
        f.date(from: s) ?? plainFormatter.date(from: s)
    }

    // MARK: - 缓存读写（Codable + 原子写；版本不符则丢弃）

    static func loadCache() -> HistoryCache {
        guard let data = try? Data(contentsOf: cacheFile),
              let c = try? JSONDecoder().decode(HistoryCache.self, from: data),
              c.version == HistoryCache.currentVersion else { return HistoryCache() }
        return c
    }
    static func saveCache(_ c: HistoryCache) {
        try? FileManager.default.createDirectory(at: StatuslineHook.supportDir, withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(c) {
            try? data.write(to: cacheFile, options: .atomic)
        }
    }
}
