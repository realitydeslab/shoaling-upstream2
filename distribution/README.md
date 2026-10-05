# Becoming Trout 第一版：Xcode 安装工程

下载 [BecomingTrout-iOS-v1.zip](BecomingTrout-iOS-v1.zip)，解压后打开 `Standalone-iOS/Unity-iPhone.xcodeproj`。这是完整的 Unity iOS 导出目录，不需要先安装 Unity。

## 在队友的 iPad 上安装

1. 在 Mac 上安装并打开 Xcode；本工程由 Unity 6000.3.21f1 和 Xcode 26.6 导出，建议使用同版本或更新的 Xcode。
2. 将 iPad 用数据线连接 Mac，解锁并信任这台电脑。
3. 打开解压后的 `Standalone-iOS/Unity-iPhone.xcodeproj`，选择 `Unity-iPhone` scheme 和连接的真实 iPad。
4. 在 `Unity-iPhone` target 的 `Signing & Capabilities` 中开启自动签名，选择自己有权限的开发团队。若应用标识被其他团队占用，修改为自己独有的 Bundle Identifier。
5. 如果 Xcode 提示需要 Developer Mode，在 iPad 的设置 → 隐私与安全中开启开发者模式并重启。
6. 点击 Run，安装后允许摄像头权限。之后可以拔掉数据线，独立运行；体验时无需网络。

ZIP 已排除本机 xcuserdata 和 .DS_Store，没有包含开发证书或私钥。签名团队仍需由安装者在 Xcode 中选择。

## 下载大文件

ZIP 通过 Git LFS 保存。网页下载时打开 ZIP 文件页面，点击 **Download raw file**。如果下载了整个 Git 仓库，请安装 Git LFS，然后在仓库根目录运行 `git lfs pull`；不要把只有几行文本的 LFS 指针当作 ZIP。

这是第一版源代码提交 `6ba98f1` 对应的完整导出，包含当前安装所需的音频、模型、数据、库和 Xcode 工程。
