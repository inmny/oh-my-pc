# 本地弹幕 API

Oh My PC 可以在本机启动一个 HTTP 接口，让其他应用复用现有弹幕通知链路。接口默认关闭，只监听回环地址，不会暴露到局域网。

## 启用

1. 打开“设置”。
2. 在“本地弹幕 API”中启用接口并设置端口，默认端口为 `39417`。
3. 保存设置。监听会立即生效；端口被占用时，应用会显示错误并保留原有监听状态。

基础地址为 `http://127.0.0.1:<端口>`。

## 发送弹幕

请求：

```http
POST /api/v1/danmaku
Content-Type: application/json
```

请求体：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `source` | 否 | 调用方来源标签，最多 80 个字符；缺省或空白时使用 `local-api` |
| `body` | 是 | 弹幕正文，去除首尾空白后为 1 至 1000 个字符 |
| `title` | 否 | 标题，最多 120 个字符；缺省或空白时使用 `Oh My PC` |
| `severity` | 否 | `info`、`warning` 或 `critical`，默认 `info` |

成功持久化并排队投递后返回 `202 Accepted`：

```json
{"id":"5ec9c901251d46f587834630529a29c9","createdAt":"2026-08-16T10:00:00.0000000+00:00"}
```

`id` 是通知中心中的记录 ID，`createdAt` 是 UTC 创建时间。响应表示通知已经写入本机数据库并排队交给桌面渠道，不保证弹幕窗口最终完成显示。

字段无效或 JSON 格式错误时返回 `400 Bad Request`；请求体超过 16 KiB 时返回 `413 Payload Too Large`；一分钟内超过 60 次请求时返回 `429 Too Many Requests`；通知无法持久化时返回 `500 Internal Server Error`。

PowerShell 示例：

```powershell
$body = @{
    source = 'build-agent'
    title = '构建任务'
    body = '测试已经通过'
    severity = 'warning'
} | ConvertTo-Json

Invoke-RestMethod `
    -Method Post `
    -Uri 'http://127.0.0.1:39417/api/v1/danmaku' `
    -ContentType 'application/json' `
    -Body $body
```

curl 示例：

```bash
curl -X POST http://127.0.0.1:39417/api/v1/danmaku \
  -H 'Content-Type: application/json' \
  -d '{"source":"build-agent","title":"构建任务","body":"测试已经通过","severity":"warning"}'
```

## 健康检查

```http
GET /health
```

正常响应：

```json
{"status":"ok"}
```

## 安全边界

- 服务仅绑定 `localhost`，同一局域网内的其他设备无法访问。
- 接口不要求令牌，因此这台电脑上的任意进程都可以发送弹幕。
- 每分钟最多接受 60 条通知，超出部分返回 `429` 且不会写入通知历史。
- 成功请求的标题、正文、来源和级别会保存在本机通知历史中；应用启动、保存保留期设置以及后续通知发布时，会按当前保留时间清理过期记录。
- 未启用跨域资源共享（CORS），网页前端不能直接跨域调用该接口。
