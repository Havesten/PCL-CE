# Debian 13：EasyTier 会合节点与 WebUI

这是为本项目整理的部署步骤，依据下列 EasyTier 官方文档。尚未在你的 Debian 13 服务器上执行；请先部署并进行双客户端验证。

- [搭建共享节点（含关闭数据转发）](https://easytier.cn/guide/network/host-public-server.html)
- [使用 Web 控制台](https://easytier.cn/guide/network/web-console.html)
- [下载](https://easytier.cn/guide/download.html)
- [官方版本 v2.6.4](https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4)
- [Web 服务参数源码 v2.6.4](https://github.com/EasyTier/EasyTier/blob/v2.6.4/easytier-web/src/main.rs)

## 角色与域名

`et.starvm.top` 的 A 记录直接指向服务器公网 IPv4，不开启普通 HTTP CDN 代理。只有服务器确实支持 IPv6 且端口可达时才添加 AAAA。客户端会合地址为 `tcp://et.starvm.top:11010`。

ET 节点只交换发现、路由与打洞 RPC 等控制信息。官方配置为 `--relay-network-whitelist --relay-all-peer-rpc`，空白名单禁止其他虚拟网的数据转发。客户端还启用 `--p2p-only`，禁止游戏流量回退到中继。不要给节点设置与玩家房间相同的网络身份。不要启用 private-mode，否则会拒绝来自玩家房间的共享连接。

房间码在启动器本地包含网络身份和游戏端口，无需在 ET WebUI 注册房间。持有码的人可以尝试加入，请私下分享。NAT 类型只是条件评估，只有实际 P2P 连通才代表成功。

## 安装命令行程序

以下以 Debian 13 amd64 为例；arm64 应改用 aarch64 发布包。服务器已有 Docker 或其他服务时先核对端口。

```sh
sudo apt update
sudo apt install -y ca-certificates curl unzip
mkdir -p /tmp/pclce-easytier-v2.6.4
cd /tmp/pclce-easytier-v2.6.4
curl -fL -o easytier.zip https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-linux-x86_64-v2.6.4.zip
unzip easytier.zip
find . -name easytier-core -o -name easytier-cli
```

从解压后的实际路径安装 `easytier-core` 与 `easytier-cli`，例如解压目录为 `easytier-linux-x86_64` 时：

```sh
sudo install -m 0755 easytier-linux-x86_64/easytier-core /usr/local/bin/easytier-core
sudo install -m 0755 easytier-linux-x86_64/easytier-cli /usr/local/bin/easytier-cli
/usr/local/bin/easytier-core --version
/usr/local/bin/easytier-core --help
sudo useradd --system --no-create-home --shell /usr/sbin/nologin easytier
```

如 easytier 用户已存在，跳过 useradd。本会合节点不创建 TUN，不需要 NET_ADMIN 或 root 权限。

创建 `/etc/systemd/system/easytier-meet.service`：

```ini
[Unit]
Description=EasyTier PCLCE rendezvous (RPC only)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=easytier
Group=easytier
ExecStart=/usr/local/bin/easytier-core --no-tun --hostname PublicServer_starvm --network-name starvm-rendezvous -l tcp://0.0.0.0:11010 --rpc-portal 127.0.0.1:15888 --relay-network-whitelist --relay-all-peer-rpc --console-log-level info
Restart=on-failure
RestartSec=3
LimitNOFILE=1048576
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now easytier-meet
sudo systemctl status easytier-meet --no-pager
sudo journalctl -u easytier-meet -n 100 --no-pager
sudo ss -lntp | grep -E '11010|15888'
```

公网安全组和主机防火墙放行 TCP 11010。RPC 15888 保持本机访问。若需要服务器接受 IPv6，请依官方监听配置加入相应 IPv6 监听，并验证无端口占用。客户端实际 P2P 打洞可能使用 UDP，这与到会合节点的 TCP 连接是两条不同路径。不要在客户端封禁 UDP 后期待 TCP 会合能保证打洞成功。

## 可选：WebUI（官方内嵌前端）

WebUI 不是房间码服务器，也不是会合服务本身。以下遵循官方 Docker 方式，WebUI 与会合进程独立运行。先按 [Docker 官方 Debian 安装文档](https://docs.docker.com/engine/install/debian/) 安装适用于 Debian 13 的 Docker Engine。

```sh
sudo mkdir -p /opt/easytier-web/data
sudo docker pull easytier/easytier:latest
sudo docker run -d --name easytier-web --restart unless-stopped \
  --entrypoint easytier-web-embed \
  -v /opt/easytier-web/data:/app \
  -p 127.0.0.1:11211:11211 \
  -p 127.0.0.1:22020:22020/tcp \
  easytier/easytier:latest \
  --db /app/et.db \
  --api-server-port 11211 \
  --config-server-port 22020 \
  --config-server-protocol tcp
sudo docker logs --tail 100 easytier-web
```

这里使用官方文档的 latest 镜像，后续升级前应记录并固定经过测试的镜像摘要，避免版本随时间变化。核心会合进程与 WebUI 的版本应兼容。数据目录存有账户和配置，应备份。

通过 SSH 隧道访问管理界面：

```sh
ssh -L 11211:127.0.0.1:11211 user@et.starvm.top
```

本地浏览器打开 `http://127.0.0.1:11211`，API 主机地址也填写此地址。按官方文档注册账号。官方提示存在默认账户 admin/admin 与 user/user；在管理界面确认并处理这些账户，管理端不要直接暴露公网。

若希望公开 HTTPS 管理入口，可使用独立域名（例如你自行创建的管理子域名）反向代理到 127.0.0.1:11211，并按官方说明配置 API 地址、证书和访问保护。该 HTTPS 页面可经 HTTP CDN，但 TCP 11010 会合端口仍需直连；不要把会合域名切成 HTTP CDN 代理。

### 让会合节点由 WebUI 管理

独立 CLI 运行的节点不会自动出现在 WebUI。若需要 WebUI 实际管理该节点，先完成账号注册，再停止 easytier-meet（避免两个实例抢 11010），使用：

```sh
easytier-core -w tcp://127.0.0.1:22020/YOUR_WEBUI_USERNAME
```

在 WebUI 中为该机器创建网络配置：不分配虚拟 IP、开启 no-tun、监听 TCP 11010、hostname 为 `PublicServer_starvm`、独立网络名 `starvm-rendezvous`、关闭 private-mode、转发网络白名单为空、开启 relay-all-peer-rpc。检查生成配置及日志，确认白名单空值被实际保留。将 core 的 -w 命令按前面的 systemd 方式持久运行，替换原 ExecStart；不要同时启动两套 core。

本例配置下发只监听服务器本机 TCP 22020；如果以后管理其他机器，需要额外开放配置下发端口或部署官方支持的 WS/WSS 管理通道。不要把 22020 当成玩家连接端口。

## 验收

1. 两位玩家分别检测 NAT，默认会合节点使用 `tcp://et.starvm.top:11010`。
2. 房主选择实际游戏端口并启动；玩家粘贴房主的完整房间码加入。
3. 本机启动不代表组网成功；只有看到对端路径 P2P 并实际连接游戏才算成功。
4. 服务器 `easytier-cli --rpc-portal 127.0.0.1:15888 -o json peer` 可辅助检查会合连接；WebUI 管理模式下 RPC 地址以其实际配置为准。
5. 用不同外网的两台设备测试，确认客户端带 p2p-only、服务器禁用数据转发；无 P2P 时游戏应无法通过该节点通信。
6. 节点不可达、对方未启动、房间码不一致不应直接宣称 NAT 不支持；应分别报错。不要把同一局域网成功当成跨 NAT 验证。

## 若以后改成云控短房间码

推荐云控只负责短码创建、查找、心跳、过期和关闭，ET 仍负责会合与打洞；游戏数据保持双方直连。创建/查找/心跳接口必须禁止 CDN 缓存，使用不可猜测的房间凭证和房主管理令牌，并对短码查询限速。现有客户端 RelayApi 使用的实际前缀是 `https://api.fuukir.cn/pclce-api/api/v1`；用户提供的 `/pclce-api/v1` 根路径目前返回 404，不可据此凭空假定后端已提供房间接口。新增接口需要先取得云控后端源码和契约。
