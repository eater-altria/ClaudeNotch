APP_NAME  = ClaudeNotch
BUNDLE    = $(APP_NAME).app
BUNDLE_ID = com.claudenotch.app
CONFIG    = release
DMG       = $(APP_NAME).dmg

# —— 分发参数（命令行/环境变量传入）——
# DEV_ID         : "Developer ID Application: Your Name (TEAMID)"
# NOTARY_PROFILE : notarytool 的 keychain 凭证名（见下方 store-credentials）
DEV_ID         ?=
NOTARY_PROFILE ?= ClaudeNotch-Notary

.PHONY: all build bundle run clean universal dist dist-sign dist-notarize dist-staple dmg verify _assemble

# ============ 本地开发：ad-hoc 签名（仅本机/愿意绕过 Gatekeeper 的用户）============
all: bundle

build:
	swift build -c $(CONFIG)

bundle: build
	$(MAKE) _assemble BIN="$$(swift build -c $(CONFIG) --show-bin-path)/$(APP_NAME)"
	codesign --force --sign - --identifier $(BUNDLE_ID) $(BUNDLE)
	@echo "✅ 本地 .app（ad-hoc 签名）"

run: bundle
	open $(BUNDLE)

# ============ 分发：Developer ID 直签 + 公证（App Store 外）============
# 准备（一次性，保存公证凭证到钥匙串）：
#   xcrun notarytool store-credentials $(NOTARY_PROFILE) \
#     --apple-id you@example.com --team-id TEAMID --password <App专用密码>
# 出包：
#   make dist DEV_ID="Developer ID Application: Your Name (TEAMID)"
dist: universal dist-sign dist-notarize dist-staple dmg verify
	@echo "✅ 已签名 + 已公证 + 已装订：$(DMG)"

# 通用二进制（arm64 + Intel）并组装 .app
universal:
	swift build -c $(CONFIG) --arch arm64 --arch x86_64
	$(MAKE) _assemble BIN="$$(swift build -c $(CONFIG) --arch arm64 --arch x86_64 --show-bin-path)/$(APP_NAME)"

# Developer ID 签名（硬化运行时 + 安全时间戳，二者均为公证所必需）
dist-sign:
	@test -n "$(DEV_ID)" || (echo "✗ 需要 DEV_ID=\"Developer ID Application: ...\""; exit 1)
	codesign --force --options runtime --timestamp --sign "$(DEV_ID)" $(BUNDLE)
	codesign --verify --strict --verbose=2 $(BUNDLE)

# 提交公证并等待结果
dist-notarize:
	ditto -c -k --keepParent $(BUNDLE) $(APP_NAME)-notarize.zip
	xcrun notarytool submit $(APP_NAME)-notarize.zip --keychain-profile "$(NOTARY_PROFILE)" --wait
	rm -f $(APP_NAME)-notarize.zip

# 把公证票据装订进 .app（离线也能通过 Gatekeeper）
dist-staple:
	xcrun stapler staple $(BUNDLE)

# 打 DMG；若有 DEV_ID 则连 DMG 一并签名/公证/装订
dmg:
	rm -f $(DMG)
	hdiutil create -volname "$(APP_NAME)" -srcfolder $(BUNDLE) -ov -format UDZO $(DMG)
	@if [ -n "$(DEV_ID)" ]; then \
		codesign --force --timestamp --sign "$(DEV_ID)" $(DMG); \
		xcrun notarytool submit $(DMG) --keychain-profile "$(NOTARY_PROFILE)" --wait; \
		xcrun stapler staple $(DMG); \
	fi

# 验证签名/公证/Gatekeeper
verify:
	@echo "— codesign —"; codesign --verify --deep --strict --verbose=2 $(BUNDLE) || true
	@echo "— Gatekeeper —"; spctl -a -t exec -vvv $(BUNDLE) || true
	@echo "— 装订票据 —"; xcrun stapler validate $(BUNDLE) || true

# 内部：组装 .app 包（BIN = 已编译可执行文件路径）
_assemble:
	rm -rf $(BUNDLE)
	mkdir -p $(BUNDLE)/Contents/MacOS $(BUNDLE)/Contents/Resources
	cp "$(BIN)" $(BUNDLE)/Contents/MacOS/$(APP_NAME)
	cp Resources/AppIcon.icns $(BUNDLE)/Contents/Resources/AppIcon.icns
	cp Resources/MenuBarIcon.png $(BUNDLE)/Contents/Resources/MenuBarIcon.png
	cp Resources/litellm_prices.json $(BUNDLE)/Contents/Resources/litellm_prices.json
	cp Resources/Info.plist $(BUNDLE)/Contents/Info.plist

clean:
	rm -rf .build $(BUNDLE) $(DMG) $(APP_NAME)-notarize.zip
