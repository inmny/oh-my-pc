# 读取同花顺远航版弹窗通知

把同花顺远航版的弹窗通知（快讯、公告、预警等推送）接入 Oh My PC 的通知链路（弹幕/托盘/通知中心）。本文记录 2026-08-28 在本机（远航版 12.1.1.4，安装于 `C:\同花顺远航版`）调查得出的结论与实施方案，尚未实现。

## 结论

**不要抓弹窗窗口，直接读它落盘的通知数据文件。**

远航版把到达的每一条推送实时写入：

```
C:\同花顺远航版\bin\users\<账号>\NotifCenter\NotifInfos.xml
```

文件为 GBK 编码 XML，随消息到达即整棵重写（实测观察期间 18:28、18:31 两次更新），包含标题、正文全文、原文链接、精确到秒的时间与已读标记。数据比弹窗本身更全（弹窗正文抓不到的部分文件里都有），且弹窗被关闭、错过或人不在电脑前都不丢消息。

## 为什么不能走窗口抓取

远航版是 .NET + CEF 混合应用：WPF 外壳（`bin\happ.exe`），界面内容全部由 Chromium 离屏渲染（OSR）绘制。实测：

- 主窗口与弹窗窗口在 UIA（Windows 辅助功能树）中均为黑盒，只有空的 `CustomWindowAutomationPeer`；
- 弹窗窗口（如"股票预警"）内部没有任何子 HWND，没有 `WM_GETTEXT`/`WM_GETOBJECT` 的可命中目标；
- 因此 UI Automation、`WM_GETTEXT`、CefSharp 辅助功能桥等窗口读取路线全部不可行，只剩 OCR——而落盘文件使 OCR 也没有必要。

## 数据文件

- 路径：`bin\users\<账号>\NotifCenter\NotifInfos.xml`；账号目录以登录账号命名（如 `mx_imdpm0ftu`），同一安装可能存在多个账号目录，读取最近修改的那个。
- 编码：`<?xml version="1.0" encoding="GBK"?>`，按 GBK 解码（.NET 用 `Encoding.GetEncoding("GBK")`，需要 `System.Text.Encoding.CodePages` 并注册 `CodePagesEncodingProvider`）。
- 结构：`<hevo>` 下按通知类型分组（当前只观察到 `NotifId7` = 快讯/公告推送；预警触发预期也会落到此文件，类型值未知，读取器不应依赖具体 type）。
- 条目字段（均为属性）：

| 属性 | 说明 |
| --- | --- |
| `type` | 通知类型分组键（如 `NotifId7`） |
| `title` | 标题（股票名 + 事件摘要） |
| `message` | 正文全文 |
| `url` | 原文链接（`news.10jqka.com.cn`） |
| `time` | 到达时间，`yyyy/MM/dd HH:mm:ss` 本地时间，新条目在文件头部 |
| `isread` | 用户在远航版内点开后翻转，不参与去重 |

- 写入方式：整文件重写（非追加）。解析需容忍"读到写了一半"的瞬间——解析失败按原水位保留、下次重读。

## 实施方案

在 Infrastructure 层新增 `TonghuashunNotifier`，模式复用现有 `DshUsageCollector` + `LocalUsageWorker` 的做法：

1. **定位文件**：启动时枚举 `<安装目录>\bin\users\*\NotifCenter\NotifInfos.xml`，取 `LastWriteTimeUtc` 最新者；安装目录固定为 `C:\同花顺远航版`（也可后续做成设置项）。文件不存在则静默空闲，不报错。
2. **监听刷新**：`FileSystemWatcher` 监听该文件所在目录 + 轮询兜底（如每 60 秒），事件加防抖（远航版批量推送时会在短时间内多次重写文件）。
3. **解析与水位**：GBK 解码、解析全部条目，取 `time` 晚于上次水位的为新消息；水位持久化到设置或内存即可（重启后重复一条可接受，或水位入库）。解析失败（写入中）保持水位不变。
4. **转发**：每条新消息调 `INotificationSink.PublishAsync`，`Source = "thshy"`、`Title = item.title`、`Body = item.message`、`Channels = Danmaku | Tray`、`Severity = Info`（可选增强：标题含"跌停/停牌/异动/立案"等关键词升 Warning）。
5. **去重**：按（`time`, `title`）作幂等键即可；`isread` 会随用户阅读翻转，不能作键。
6. **开关**：加设置项（默认关闭，因为依赖本机装有远航版且已登录过），放入"弹幕"或独立"数据源"分区，中英双语资源同步。

## 边界与注意

- 未登录远航版时文件不更新；登录后才继续推送，天然断点续传（水位之前的消息不会再补，重启后水位如丢失会重放整文件，故水位最好入库）。
- 快讯在盘后仍持续推送（实测 18:31 仍有公告），不需要交易时段判断。
- 远航版自身升级可能改变文件格式（版本号目录 `cache\12.1.1.6` 暗示自动更新），解析需容错、格式不匹配时静默停用并记日志。
- 调查期间打开过的辅助材料：点铃铛弹出的"股票预警"窗口是独立顶层 WPF 窗口（700×500，标题可枚举）——若将来要做"弹窗出现即通知"的兜底，可用 `SetWinEventHook(EVENT_OBJECT_SHOW)` 按进程过滤取标题，但内容仍需读文件，此路线仅作备选。

## 验证记录

2026-08-28：远航版 12.1.1.4 自动登录运行中；`NotifInfos.xml` 66 条、全部 `NotifId7`，含当日盘后财报快讯与公告；GBK 解码正常、字段完整；两次观察期间文件随新推送实时更新。
