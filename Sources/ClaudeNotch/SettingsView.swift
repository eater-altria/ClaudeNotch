import SwiftUI

struct SettingsView: View {
    @ObservedObject var settings: SettingsStore

    var body: some View {
        Form {
            Section("外观") {
                Picker("配色", selection: $settings.appearance) {
                    ForEach(AppearanceMode.allCases) { mode in
                        Text(mode.label).tag(mode)
                    }
                }
                .pickerStyle(.segmented)
            }

            Section("通用") {
                Toggle("开机自启动", isOn: $settings.launchAtLogin)
                Toggle("启用灵动岛", isOn: $settings.islandEnabled)
            }

            Section("通知") {
                Toggle("额度 / 上下文通知", isOn: $settings.notificationsEnabled)
                Text("额度用到 80% / 95%、会话上下文 ≥90% 时发系统通知。")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("显示器") {
                Text("勾选在哪些显示器上显示挂件；都不选 = 自动（刘海屏 / 主屏）。")
                    .font(.caption).foregroundStyle(.secondary)
                ForEach(settings.screenOptions) { opt in
                    Toggle(opt.label, isOn: Binding(
                        get: { settings.selectedScreens.contains(opt.id) },
                        set: { on in
                            if on { settings.selectedScreens.insert(opt.id) }
                            else { settings.selectedScreens.remove(opt.id) }
                        }
                    ))
                }
            }

            Section {
                Text("「花费」按 API 单价折算估计，订阅用户并不按此单独计费。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 400, height: 460)
    }
}
