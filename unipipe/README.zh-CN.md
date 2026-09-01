# UniPipe

[English](README.md) · **中文**

[yucchiy/UniCli](https://github.com/yucchiy/UniCli) 的分支，目标是把 Unity 编辑器自动化收敛成**一个库**：一套命令定义，多个前端（CLI、MCP、CI、运行中的 player），以及那些原本要在两个工具之间来回横跳才能拿到的能力。

分支新增的一切都在 `unipipe/` 下，其余目录是原封不动的上游 UniCli，所以合并上游永远零冲突。

## 为什么要分叉

UniCli 通过命名管道驱动运行中的编辑器，命令面覆盖广，还独有 Profiler、内存快照、Recorder 三个域；它是 MIT，可以分叉、扩展、再分发。

Unity 官方的 `com.unity.pipeline` 覆盖了相近的地盘，并且有一样 UniCli 给不出的东西——**进程内 C# 热重载**。但它是 Unity Package Distribution License：允许你在自己工程里用，不允许再分发，所以它**不能被合并进一个库**，只能引用或重写。

UniPipe 的做法是以 UniCli 为底座，**吸收值得要的设计，而不是搬运不能搬的代码**。

## 现在有什么

一套命令定义之下挂着四个前端和一条运行时通道：

- **命令路由层** —— 命名管道 / HTTP / MCP / CLI 只是外壳，都解码成同一个请求交给同一个 dispatcher，所以新前端自动继承单命令槽、前置条件、undo 分组。
- **写安全环** —— 前置条件由 dispatcher 统一强制（不再是每个 handler 自己第一行去 guard）、undo 按命令折叠成一步、SHA 防陈旧写、脚本写盘前的孤立编译预校验、脏场景闸门。
- **自研热重载** —— `HotReload.Apply`：改一个方法体，一条命令，活着的对象直接跑新代码，不重编译不域重载。类型字段布局变了就整类拒绝，这是它和 demo 的分界。
- **MCP 原生** —— 做成第三个传输而不是第二套实现；只暴露 8 个工具加一个逃生舱，避免在代理动手前就吃掉上下文。默认关，仅 loopback，从不碰 Unity 云。
- **事件通道** —— `Events.Poll` 带游标续读，外加 HTTP 上的 SSE 推送。缓冲区活过域重载，因为"域重载了"恰恰是客户端最需要知道、又最容易随内存一起丢掉的那件事。
- **多实例发现与路由** —— 编辑器把自己登记到 `~/.unicli/instances/`，`unicli instances` 列出全机器的编辑器，`UNICLI_PROJECT` 也接受名字。**歧义拒绝不猜**：两个同名工程时退出 1 并列出候选，而不是随便挑一个把写操作送错地方。
- **CI 降级闸** —— 命令声明自己需要什么环境（图形设备 / 会渲染的窗口），dispatcher 在 handler 跑之前拒绝。这道闸不是为了错误信息好看：无头环境下有命令会**直接把编辑器打崩**，还有命令会**退出 0 返回空帧**。
- **渲染统计** —— 编辑器侧 `Render.GetStats`、player 侧 `Debug.RenderStats`，都带 dynamic/static/instanced 合批分解。"draw call 涨了"是症状，"哪条合批路径失效了"才是问题。
- **Player 只读观测档** —— 通过 PlayerConnection 读运行中的构建：系统信息、性能、场景、层级、日志、查找对象、渲染统计。**只读，没有任何远程写命令。**

### 快速上手

```bash
unicli instances                                   # 机器上有哪些编辑器
unicli exec Editor.Status                          # 编辑器在忙什么
unicli exec Events.Poll '{"since":42}'             # 上次看之后发生了什么
unicli exec Render.GetStats                        # 这一帧画了多少批
unicli exec Connection.List                        # 找到运行中的 player 的 id
unicli exec Remote.Invoke '{"command":"Debug.RenderStats"}'
```

### 文档

| 路径 | 内容 |
|---|---|
| [`docs/plan.md`](docs/plan.md) | 整体方案：往哪走、为什么，以及整个设计绕着建的那条合规边界 |
| [`docs/porting-pipeline-to-2022.md`](docs/porting-pipeline-to-2022.md) | 把 `com.unity.pipeline` 跑在 Unity 2022.3 上的九个坑与解法（不依赖本分支，单独有用） |
| [`docs/hot-reload.md`](docs/hot-reload.md) | 把改过的方法体打进运行中的编辑器：怎么做的、什么情况下拒绝、为什么 |
| [`docs/mcp.md`](docs/mcp.md) | 从 AI 客户端驱动编辑器：工具面、错误语义、纯本地边界 |
| [`docs/events.md`](docs/events.md) | 不再轮询：游标、事件种类、推送流 |
| [`docs/instances.md`](docs/instances.md) | 找到并指定机器上的编辑器：发现、三种状态、为什么歧义要拒绝 |
| [`docs/ci.md`](docs/ci.md) | 无头下什么会坏——一次崩溃加两次静默空帧，以及现在拦住它们的闸 |
| [`docs/render-stats.md`](docs/render-stats.md) | `Render.GetStats`：批次、SetPass、合批归因，以及这些数字在哪里才是真的 |
| [`docs/player-tier.md`](docs/player-tier.md) | 读运行中的构建：`Debug.RenderStats`、High 裁剪下什么活得下来、隧道网卡下发现为什么失败 |

`unipipe/bridge/` 是一个过渡产物：它把 `com.unity.pipeline` 的命令面透过 UniCli 包成一个门面。当初存在是因为热重载曾是我们唯一真正依赖官方包的能力；自研热重载落地后，它降级成参考实现而不是依赖。

`unipipe/samples/UiHotTuning/` 演示的是程序集布局——怎么让 UI 调参代码可以热重载，同时不让 UI 库对自动化包产生编译期依赖。

## 实测到哪一步

文档里的数字全部是实测，不是声称。以下都在 **Unity 2022.3.62f3 / macOS arm64** 上跑过：

- 热重载：方法体替换 279–431 ms，帧计数跨越替换连续，无域重载。
- 事件流：一次编译产出 started/finished/reloading/reloaded 四条，**在抹掉一切内存记录的那次域重载之后仍然读得到**。
- 多实例：三个编辑器同时在跑，跨目录按名寻址通、歧义退出 1 并列出两条、死记录被剪而活的没动。
- CI 闸：`-nographics` 下截图命令原本会原生崩溃（日志 44 帧），加闸后变成退出 1 且编辑器存活（0 帧）。
- 合批归因：20 个立方体 20 种材质 = 71 批；同样的立方体换成一个开 instancing 的共享材质 = 7 批，归因为 4 个 instanced 批覆盖 68 次调用。
- Player 档：真实 development build 里 23 批 ↔ 4 批随场景切换，三角形恒为 1924（几何没变，只有合批变）。Mono 与 IL2CPP 两个后端在 High 裁剪 + 引擎代码裁剪下，8 条远程命令与 15 个计数器全部存活。
- 完整无头链路：`-batchmode` 编辑器驱动 IL2CPP/High 裁剪的 player，端到端通。
- 测试：Unity EditMode 259/259，客户端 71/71。

**同样要说清楚没测的：** 上面这一切**只在 2022.3.62f3 上验过**。仓库里带着 Unity 6000.0 与 6000.5 的样例工程，但这些能力都还没在那两个版本上跑过，而其中有几处结构上就对版本敏感（`UnityStats` 的字段集、profiler 计数器名、帧调试器那个已知搬过家的命名空间）。

其他已知边界：HTTP 与 MCP 传输仅 loopback 且**尚无鉴权**（默认关闭）；热重载只保证替换单个方法体，签名变更、新增字段、重绑已解析的调用方都还是假设；帧调试器控制受阻，试过的路径记在方案里，免得重走。

## 合规边界

Unity 于 2026-06-30 更新的服务条款，对自动化与 AI 驱动访问 Unity offerings 作了限制（§17.2 ff、gg）。Unity 员工曾**非正式地**表示这针对的是连接 Unity 云服务而非本地编辑器自动化，但条款正文并没有这么写。

因此 UniPipe 的设计是**本地优先、云隔离**：驱动你自己机器上的编辑器是核心；任何会与 Unity 云服务通信的能力一律不吸收，或默认关闭。这是一条架构边界，不是免责声明——详见方案。

## 许可

MIT，继承自 UniCli。上游代码版权归 Yuichiro Mukai（2026）；`unipipe/` 下的新增内容版权归 yangtzehina（2026），同一许可。见 `LICENSE`。

与 Unity Technologies 无隶属关系，未获其背书。本仓库不再分发任何 Unity 源代码。
