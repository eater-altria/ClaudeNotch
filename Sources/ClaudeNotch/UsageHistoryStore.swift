import Foundation
import Combine

/// 历史用量的可观察存储：后台扫描全部 transcript，发布按天聚合的 `UsageHistory`。
/// 懒构建——只在分析窗口首次打开时 refresh，不在 app 启动时跑。
@MainActor
final class UsageHistoryStore: ObservableObject {
    @Published private(set) var history = UsageHistory()
    @Published private(set) var isBuilding = false
    @Published private(set) var progress: Double?      // 0...1，首跑全量回填时显示

    private let queue = DispatchQueue(label: "com.claudenotch.history", qos: .utility)
    private var hasLoaded = false

    /// 首次打开窗口时调用：没加载过就构建一次。
    func refreshIfNeeded() { if !hasLoaded { refresh() } }

    func refresh() {
        if isBuilding { return }
        isBuilding = true
        if !hasLoaded { progress = 0 }
        queue.async { [weak self] in
            let result = HistoryScanner.build(progress: { p in
                DispatchQueue.main.async { self?.progress = p }
            })
            DispatchQueue.main.async {
                guard let self else { return }
                self.history = result
                self.isBuilding = false
                self.progress = nil
                self.hasLoaded = true
            }
        }
    }
}
