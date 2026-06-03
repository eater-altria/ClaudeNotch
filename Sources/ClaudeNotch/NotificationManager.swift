import Foundation
import UserNotifications

/// 系统通知（额度阈值 / 上下文告警）。
@MainActor
final class NotificationManager {
    static let shared = NotificationManager()

    private var enabled = false
    private var didRequestAuth = false

    /// 由设置开关驱动。开启时按需请求授权。
    func setEnabled(_ on: Bool) {
        enabled = on
        if on { requestAuthIfNeeded() }
    }

    private func requestAuthIfNeeded() {
        guard hasBundle, !didRequestAuth else { return }
        didRequestAuth = true
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    /// `sound=false` 用于「提示档」（如额度 80%）静默送达；`true` 用于「严重档」（95% / 上下文将满）出声。
    func notify(id: String, title: String, body: String, sound: Bool = true) {
        guard enabled, hasBundle else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        if sound { content.sound = .default }
        let request = UNNotificationRequest(identifier: id, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }

    // UNUserNotificationCenter 在没有 app bundle 的进程里会崩，做个保护
    private var hasBundle: Bool { Bundle.main.bundleIdentifier != nil }
}
