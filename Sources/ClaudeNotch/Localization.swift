import SwiftUI
import Combine

// MARK: - 语言

enum AppLanguage { case en, zh }

/// 用户的语言偏好（设置里可选）。`.system` = 跟随系统，匹配不到中文则用英语。
enum LanguagePreference: String, CaseIterable, Identifiable {
    case system, zh, en
    var id: String { rawValue }
    var label: String {
        switch self {
        case .system: return tr("跟随系统", "System")
        case .zh: return "中文"
        case .en: return "English"
        }
    }
}

/// 进程级的当前语言 / 汇率快照——供全局 `tr()` / `money()` 在任意线程**只读**，
/// 由 `Localization` / `ExchangeRateStore` 在主线程更新（值类型赋值，读侧无需锁）。
enum LocalizationState {
    nonisolated(unsafe) static var currentLanguage: AppLanguage = .en
    nonisolated(unsafe) static var usdToCny: Double = 7.15        // 离线默认，联网成功后覆盖
}

/// 双语取值：当前为中文返回 `zh`，否则（含未匹配到中文的系统语言）返回 `en`。
func tr(_ zh: String, _ en: String) -> String {
    LocalizationState.currentLanguage == .zh ? zh : en
}

// MARK: - 偏好存储 + 反应式刷新

/// 持有语言偏好并驱动 SwiftUI 刷新。各窗口根视图观察 `Localization.shared`，
/// 偏好一变即整棵子树重建 → 全 app 文案与货币即时切换。
@MainActor
final class Localization: ObservableObject {
    static let shared = Localization()

    @Published var preference: LanguagePreference {
        didSet {
            UserDefaults.standard.set(preference.rawValue, forKey: Self.key)
            LocalizationState.currentLanguage = Self.resolve(preference)
        }
    }

    var language: AppLanguage { Self.resolve(preference) }

    private static let key = "languagePreference"

    private init() {
        let raw = UserDefaults.standard.string(forKey: Self.key) ?? ""
        preference = LanguagePreference(rawValue: raw) ?? .system
        LocalizationState.currentLanguage = Self.resolve(preference)
    }

    /// 把偏好解析成实际语言。`.system` 时看系统首选语言是否中文。
    static func resolve(_ pref: LanguagePreference) -> AppLanguage {
        switch pref {
        case .zh: return .zh
        case .en: return .en
        case .system:
            let code = (Locale.preferredLanguages.first ?? "en").lowercased()
            return code.hasPrefix("zh") ? .zh : .en
        }
    }
}
