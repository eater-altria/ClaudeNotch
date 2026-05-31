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

            Section {
                Text("「花费」按 API 单价折算估计，订阅用户并不按此单独计费。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 380, height: 280)
    }
}
