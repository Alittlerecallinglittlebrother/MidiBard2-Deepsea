# 3.2.5.24 本地测试版验证记录

本文保留在线发布前的本地验证记录，下文“未发布”“在线清单未改动”均指该检查阶段。3.2.5.24 的在线发布说明见 STAGE-VERIFICATION.md；发布不会把下述检查变成真实多机游戏验收。

日期：2026-09-21。基于 d3df7017d8c6b965e9547ae766ec7db9a383a931（3.2.5.21），本地分支 local/song-distribution。插件 3.2.5.24 / Dalamud API 15 / Windows x64 / .NET 10。先前 3.2.5.22 和 3.2.5.23 交付包保留。

本次加入跨电脑跟随、按角色绑定的队形编辑、位置与朝向同步、站位预设保存及停止控制。复用现有演出房间和已验证小队身份；移动接收每次插件加载后默认关闭。自动与手动歌曲分发保持可用。

## 本次完成的验证

| 检查 | 结果 | 范围 |
| --- | --- | --- |
| Release 插件构建 | 0 错误，41 警告 | 插件及依赖编译；保留原工程的废弃 API、空字段等警告 |
| BardStage.Core.Tests | 276 通过、0 失败、0 跳过 | 原核心及传输逻辑，新增坐标转换、跟随距离、到位朝向、失联、手动接管、换场景/队长/成员、不可见、高差、距离及移动受阻检查 |
| MovementRuntimeChecks | 19 条 PASS 输出（含房间建立） | 实际回环 TLS 房主加两客户端，生产移动协调与状态机，测试移动驱动；跟随下发、全员停止、本机停止后立即重开、站位保存、到位回执、开始演奏、失联、队长接管、普通队员伪造拒绝、切场景、接收关闭、提交超时及迟到指令取消、断线、旧房间能力识别 |
| MovementUiChecks | 7 条 PASS 断言 | 原生 ImGui 生产页面；开关点击、八人队形建立、真实鼠标拖动、保存后重读、小窗口停止、滚动至下发按钮、换角色关闭接收 |
| 原 AutoAssignmentTests | 54 条 PASS 断言 | 原自动配器和队长显示顺序分轨回归 |
| 原 PartyPlaybackTests | 49 条 PASS 断言 | 自动/手动选曲、空曲库、手动配置优先、回执等待、取消、队长接管与旧配置失效 |
| 原 ManualAssignmentRuntimeChecks | 14 条 PASS 断言 | 真实回环 TLS 手动轨道方案分发、身份与小队验证、队长接管 |
| 原 SongSyncRuntimeChecks | 51 条 PASS 输出（含测试环境建立） | 真实回环 TLS、七端独立缓存、内容校验、慢速传输、取消与断线 |
| 当前游戏文件静态检查 | 三个签名均唯一匹配且目标位于代码段 | 地面移动输入函数与两个输入许可函数；未安装钩子、未调用游戏函数 |
| 交付检查 | 通过后见 verification/package-check.json | ZIP 完整性、版本、依赖、作者顺序、源码一致性、SHA256、前两版本文件完整保留 |
| Git 检查 | git diff --check；repo.json 未改动 | 在线清单仍为本地基线 3.2.5.21，未查询或更新远端 |

界面检查使用 1100×740 / 100% 与 760×540 / 140% 窗口；实际渲染截图保存在 verification/movement-ui。小窗口下发按钮的几何范围校验使用滚动面板可见边界，而非只检查整个窗口高度。

原生移动输入参考本机已检出的 OmenTools MovementInputController 与 Angle 中的签名和方向转换，固定提交 c66992f6df6a9c2e98fa86e2822c1208cf021478。来源与 MIT 许可已纳入第三方说明。网络协调、队形编辑、连接确认和移动状态机为本分支实现，不依赖安装 BardToolbox 或 OmenTools。

## 实现边界

- 网络检查在同一台电脑内运行多个独立 TLS 客户端，身份映射和游戏场景来自测试数据；不是多台真实电脑。
- 状态机测试使用测试移动驱动，没有让真实角色跑动或修改真实角色朝向。界面截图是原生 ImGui 测试渲染，并非游戏内截图。
- 静态签名匹配不能证明移动输入、实际站位精度、游戏碰撞、传统/标准移动模式或公网时延已验收。
- 尚未安装到游戏、执行游戏内移动或完成真实多机 FF14 验收。未发布 GitHub Release、未更新在线插件库。
- 首版不做绕障寻路、跨区导航、骑乘/飞行/游泳，跟随途中不保持固定队形。具体范围和双机验收步骤见 LOCAL-MOVEMENT.md。
- 仅在用户勾选本机接收时初始化本机地面输入钩子；关闭时释放本模块输出并禁用钩子。不直接修改角色坐标，不改变原移动模式设置。
- 独立接收会话、请求编号、短时租约与身份核对用于阻止迟到指令重启已停止的操作；收到旧房间能力声明时不发送移动扩展消息。

## 复现

在源码根目录依次运行，避免多个构建同时写入同一 bin/obj：

~~~powershell
dotnet test Stage/BardStage.Core.Tests/BardStage.Core.Tests.csproj
dotnet run --project Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj -- --movement-check ../movement-check
dotnet run --project Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj -- --movement-ui ../movement-ui
dotnet run --project Stage/BardStage.AutoAssignmentTests/BardStage.AutoAssignmentTests.csproj
dotnet run --project Stage/BardStage.PartyPlaybackTests/BardStage.PartyPlaybackTests.csproj
dotnet run --project Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj -- --manual-assignment-check ../manual-check
dotnet run --project Stage/BardStage.RuntimeTests/BardStage.RuntimeTests.csproj -- --song-sync-check ../song-check
dotnet build Midibard/MidiBard2.csproj -c Release
~~~

完整构建依赖 Dalamud API 15 SDK。交付目录保留上述成功日志、截图和静态签名检查记录；两个 ZIP 的校验值见 SHA256SUMS.txt。
