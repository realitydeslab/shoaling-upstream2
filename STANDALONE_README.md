# Becoming Trout — iPad 单机版

工作工程：本文件夹的 app。Unity 6000.3.21f1；场景 Assets/ShoalingUpstream/Scenes/StandaloneAR.unity。桌面原工程保持原样。

## 更新安装

从桌面快捷方式 Becoming Trout CG Dev 打开 builds/Standalone-iOS/Unity-iPhone.xcodeproj，选择真实 iPad，点击 Run。导出会保留既有签名团队与 Bundle Identifier。导出和模拟测试不代表已经完成真实 iPad 安装验证。

## 设计与素材

依据用户 Google 文档以及对话中的后续修改：
https://docs.google.com/document/d/1euW4CHppsscK7eSYoQ3enUdphIJ6FJXr4MWqJ6X-Tfc/edit?usp=sharing

旁白使用 Kinship/AR/Sound 中除 V1 by 08/2026 外的现有音频副本。操作按钮位于屏幕底部安全区；没有黑框或 Becoming Trout 标题。Start the Journey 开始；Repeat Prompt 回放当前提示并保留已确认选项。非人类识别和动作暂用按钮确认；输入适配层保留迁移眼镜的空间。

## 当前章节体验

- Opening：听完后确认 We are in the creek facing downstream now。
- Chapter 1：Tree Found / Gravel Found。Spawn 生成 40 个卵，呈圆形错落分布，邻卵表面间距为 0–1 个卵半径。落定等 6 秒后孵化。振动频率为本次修改前的 0.7，幅度为 0.8；孵化时间各不相同，alevin 再错开上升，随机位于地面到 iPad 高度范围的中上部分。
- Chapter 2：Deep Pool Found / Water Plants Found。Alevin 先游入当前所指的 pool，等 3 秒后播放 Growing takes patience，再在 Plants shelter you / Water carries you / And little by little you grow 旁白期间陆续变成 fry。Fry 在 pool 自然游动；第三章开始时观察行走，马上启用根据当前相机位置更新的前方跟随，不再等待向旧位置归队。Strider 出现前若尚未开始跟随也会启用。
- Chapter 3：三只 Strider 按实际相机画面分布在前方左、中、右，前后略错开，在同一水平面，位于整个 fry 鱼群的前方，水平朝向各自随机；让挡住它们的鱼往下让开。Receive water strider’s offer 后三条不同的鱼错开接近，用真实鱼网格鼻端作为接近位置，等 1 秒，再让 Strider 在 0.8 秒内滑入鱼头消失。鱼先转向再游回原鱼群；全部归队后等 3 秒，全群错开开始并在 3 秒窗口内完成长大。
- Heron 与 nest 整组中心在用户右前方 45°、约 3.5 m 处。各模型视觉尺寸在上一版基础上增大 1.2 倍，不修改各模型的相对位置。Heron/nest 锚点间距仍约 0.933 m，Baby 的原始位置保持一致。出现时及后续同行时，鱼群避开画面中的 Heron/Nest 区域。
- Offer to Heron：使用原鸟模型的 Lower Head / Turn / Lower Head 曲线。先缓慢降低嘴部并等鱼游到嘴，接触后才缓慢抬头、转身、喂 Baby；携鱼始终跟随实际嘴部骨骼，脚的位置锁定。整组完成后保留在原空间位置，直到 fry 变为 rainbow trout 后用 3 秒淡出。
- Chapter 4：Rainbow trout 掉头后与参与者一起向上游前进。Jump 峰值参数 0.56 m；每次留下 1–3 条，同行鱼少于 5 条后停止减员，至少保留 2 条产卵。
- Chapter 5：We are home now 后其他鱼分别转向四周、缓慢游开，边游边逐渐淡出；两条产卵鱼留下，旁白与其他鱼离开并行，不等待它们游完长距离。随后，留下两条贴近地面产卵，然后贴地游向前方、翻转淡出。The End 加粗居中，宽度为屏幕的 1/3。

## 鱼群移动

游动动画速度为本次修改前的 1.2 倍（原始动画速度的 0.8）。自然同行及脚本游动采用稳定 0.36 m/s 的巡游目标，pool 游动为 0.24 m/s；加减速限制为 0.36 m/s²，同行时平滑估计用户的行走速度，落后时平滑追上；速度变化受加速度约束，最高 1.44 m/s，普通独立游动保持 0.36 m/s。移动距离受单帧速度限制，深度与水平移动合成后也不能超过该限制。

同行鱼的距离约为 iPad 前方 0.5–2 m，随机高度处于地面与 iPad 之间，最终鱼头朝共同前方 ±2°。轻微位移和小于 20° 的水平转动保持世界位置；较大变化后缓慢移动或转向。鱼先转向，再沿鱼头方向游动，不倒退。到达后恢复共同朝向是后台收尾，不阻塞后续旁白或按钮；到达判定允许小幅上下浮动。章节间原有 10 秒邀请等待缩短为 3 秒；明确要求的第一章 6 秒及第二章 3 秒仍保留。鱼的自然浮动只作小幅上下变化。

放下倒转 iPad 时，使用原始重力及 AR 相机姿态暂停位置跟随和方向更新；恢复举起后才恢复。识别实际挂绳动作仍需真机检查。

## 验证记录

最新结果见 validation/continuity-verification.json，测试日志与结果以 continuity 开头。此前版本检查保留为 steady-fish 文件。模型实际渲染检查见 validation/strider-model-audit.png 和 heron-group-audit.png。

原工程 Strider 实际素材为 Cricket_LOD0（蟋蟀），本版沿用现有模型。原 HERON 和 NEST 模板保持原样，运行时修改尺寸与交互。原远程控制场景仍保留，iPad 单机场景无需电脑服务或 iPad B。

本次验证完成：238 项 EditMode 规则测试、4 项不同的 PlayMode 场景测试通过；其中完整流程与模型接触还在 Unity 图形模式下复验。实际场景三只 Strider 截图为 validation/steady-fish-striders-runtime.png；Heron 接鱼截图为 validation/steady-fish-heron-contact-runtime.png。最终 iOS 导出成功、0 错误，原签名团队和应用标识保留。尚未在真实 iPad 上体验本版。

最新连贯性更新：239 项 EditMode 测试和 4 项 PlayMode 场景测试通过。新增验证覆盖第二章耐心旁白先于成长、成长与后续旁白同时进行、6 秒连续行走时 fry 位于参与者前方、向下 45° 看溪流时三只 Strider 完整可见且在 fry 前方，以及第五章其他鱼向四周游开而不阻塞旁白。

最新 iOS 导出成功，0 错误，原签名与应用标识保留。此更新尚未在真实 iPad 上测试。


### 2026-10-05 Life-stage and walking-trigger update

- Eggs use random 3D rotations. Egg/alevin and alevin/fry transitions crossfade over 1.5 seconds. The old alevin grows during fade-out.
- Hatched alevins immediately swim toward the participant's lower front, without the previous extra 0-2.5 second hold. Gentle independent hovering continues. Chapter 1 keeps following enabled.
- Chapter 3: wait 15 seconds after Notice, then play Look toward the creek and reveal three depth-staggered Striders with random headings on one water plane.
- Heron: after Continue downstream, wait for 1 metre of horizontal displacement, then wait 15 seconds. Ignore hides the group and restarts the walking condition. Feeding approach speed is 1.5 times the previous speed.
- Wait 2 seconds between Your gift helps their lives continue and Continue downstream.
- Each Jump leaves 3-5 fish when more than 5 are following, retaining at least two breeding fish. Detached fish remain for 15 seconds, then fade over 3 seconds. At 5 or fewer, further jumps retain the entire following group.
- Four standalone PlayMode checks passed with GPU rendering. Physical iPad experience still requires user testing.


### 2026-10-05 Opaque life-stage transitions and newborn movement

- All three life-stage transitions fade the new model linearly from alpha 0 to 1 over exactly 2 seconds. Then restore the original shader, opaque blend mode and depth writes, preventing permanently translucent fish.
- Root facing is rebuilt using horizontal yaw only, independent of random egg rotation; ordinary swimming maintains upright root orientation. Scripted jumps and feeding retain their intended poses.
- All fish gently hover independently in X/Y/Z around their spatial position.
- Alevin destinations are 0.5-1 metre horizontally ahead of the participant and 0.15-0.4 metre below the device. Their translation speed is 0.6 times the previous speed. Newborn buoyancy continues while turning; destination height is updated throughout ascent.
- Narration segments are PCM sample slices rather than frame-polled stops on the full MP3. The heron gift sentence ends at 39.10 seconds, followed by a 2-second wait; Continue downstream starts from 39.52 seconds.

Validation: all 5 standalone PlayMode checks passed, including real-model opacity restoration, upright poses, walking follow and the full chapter flow. Device install is performed by the user from the exported Xcode project.


### 2026-10-05 Alevin 90% opacity and ground Striders

- Only the egg-to-alevin transition now targets alpha 0.9 linearly over 2 seconds. Fry and rainbow trout still finish opaque. Alevin translation is 0.6 times the previous version (0.36 of the earlier base scale).
- Three Striders share the actual ground level (root 5 cm above it), with depth offsets and random yaw. Their row is fitted to the current camera view. Fish clear the row sideways using their complete projected body bounds, including a margin for their wider silhouette during a turn.
- Extend the heron gift narration slice through 39.40 seconds to preserve the final continue tail; resume the next Continue downstream from 39.52 seconds after the 2-second pause.

Validation: all five standalone PlayMode checks passed. The actual GPU image was inspected and all three Striders were visibly unobstructed. iPad testing remains manual.


## 给队友直接安装

完整 Xcode 导出已打包为 [distribution/BecomingTrout-iOS-v1.zip](distribution/BecomingTrout-iOS-v1.zip)，通过 Git LFS 保存。队友无需 Unity，下载解压后可用 Xcode 安装到自己的 iPad。具体步骤见 [安装说明](distribution/README.md)。
