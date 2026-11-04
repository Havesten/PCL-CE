|          |                         |                         |
| -------- | ----------------------- | ----------------------- |
| **简体中文** | [English](README-EN.md) | [繁體中文](README-ZH_TW.md) |

<div align="center">

![Logo](<Plain Craft Launcher 2/Images/icon.ico>)

# PCL CE FIX

基于 PCL 社区版二次开发的第三方分支

[本分支发布页](https://github.com/Havesten/PCL-CE/releases) |  
[社区版上游](https://github.com/PCL-Community/PCL-CE) |  
[PCL 原作者](https://github.com/Meloong-Git/PCL)

[提交问题](https://github.com/Havesten/PCL-CE/issues/new/choose) |  
[更新日志](https://github.com/Havesten/PCL-CE/releases)

</div>

## 这是什么

PCL（Plain Craft Launcher）是龙腾猫跃开发的 Minecraft 启动器。PCL CE 是社区在其开源代码基础上继续开发的版本。

本仓库是 **PCL CE 的第三方分支**，由个人独立维护。它不是 PCL 或 PCL CE 的官方版本，也不代表上游的任何立场。

## 与原版 PCL CE 的差异

### 新增：下载节点优选

原本只能在「官方源」和「镜像源」之间二选一，没法控制具体连哪台节点。现在会先对每台候选节点做 TCP 与 TLS 握手计时，多轮取中位数抑制抖动，锁定延迟最低的一个，后续请求直接连它 —— URL、SNI、证书校验全部保持不变。

覆盖 16 个域名，分四组：

| 分组         | 域名                                                |
| ---------- | ------------------------------------------------- |
| Mojang 本体  | piston-meta、launchermeta、launcher、resources 等 6 个 |
| 模组加载器      | Forge、NeoForge、Fabric 的 maven 与列表站，共 5 个          |
| CurseForge | api、edge、mediafilez                               |
| Modrinth   | api、cdn                                           |

Cloudflare 站点会额外改用通用优选 IP 池（`cloudflare.182682.xyz` 的解析结果），整个池只测速一次，所有 CF 域名共用结果 —— 因为 CF 是 Anycast，一组优选 IP 对所有接入 CF 的域名通用。非 CF 站点（AWS CloudFront、Fastly、Azure）判定后照常走自己的解析结果，不会误用。

入口在「设置 → 下载 → 节点优选」，可随时重测或固定节点。

### 调整：更新源

- 移除 Mirror 酱内测通道及对应的 CDK 输入框
- 更新地址改为私有服务端，协议与 Minio 源兼容
- 关闭在线更新时不再弹社区版公告

### 删除重构：联机部分

原「大厅」入口及其依赖的 Natayark 账号登录已移除，联机整体删除后重建：从「工具」页提到标题栏，成为与启动、下载、设置、工具并列的入口，目前对外提供中继联机。

- **中继联机**：房主本地端口由中继服务器映射到公网，不需要公网 IP，双方都在 NAT 后面也能连。需要邮箱验证码登录（首次登录即注册，带人机验证），登录后可看月流量、限速与端口名额；这些限制由服务端下发并在服务端执行，改本地配置无法绕过。隧道协议为自研 PTP v1，客户端与服务端全部内置，不需要任何外部二进制
- **P2P 直连已撤下**：页面实现保留在 `Pages/PageLink/PageLinkP2P`，导航里不再出现——纯直连要求至少一方具备公网 IP，双方都在 NAT 后面就必然连不上，作为默认入口体验太差。需要时在 `PageLinkLeft.xaml` 取消 `ItemP2P` 注释即可恢复

### 变更：账号

- 新建档案只提供「微软账号」与「第三方验证」两种方式，**离线登录入口已移除**。已存在的离线档案不受影响，仍可正常启动

### 调整：界面

- 移除主页右侧常驻的社区版提示卡片
- 标题栏 Logo 后加 `FIX` 标识，便于和上游构建区分
- 默认游戏窗口标题改为 `玩家：{user} 登录方式：{login} 版本：{version} 时间：{date} {time}`
- 「工具」页里的联机入口已移除，联机统一走顶栏的「联机」页
- P2P 联机页暂时隐藏：导航里只留「中继联机」，进联机页直接落在中继面板；P2P 页代码与其房间码 / 对端地址 / 协议栈三选一逻辑都原样保留，恢复只需在 `PageLinkLeft.xaml` 里取消 `ItemP2P` 的注释
- 「设置 → 联机（EasyTier）」整页删除：用户名、协议偏好、延迟优先、对称 NAT 端口猜测、Cli 调试输出这些选项只在 EasyTier 直连时生效，直连入口隐藏后已无处可达，连「工具」分类与「隐藏功能」里对应的勾选框一并移除

## 功能

以下均为上游已有能力，本分支原样保留：

- 版本安装、整合包导入、模组 / 资源包 / 存档管理
- 模组与资源下载（CurseForge、Modrinth）
- **联机**：EasyTier 隧道、公共中继节点与自建中继服务器配置（本分支在此基础上重构，见上文）
- 微软正版登录、皮肤与披风管理、多账号切换
- Java 自动检测与安装、崩溃分析、日志导出

## 下载

从 [Releases](https://github.com/Havesten/PCL-CE/releases/latest) 取最新构建，单文件 exe，解压即用，无安装程序。

环境要求与上游一致：Windows 10 1809 (17763) 及以上，需安装 [.NET 10 Desktop Runtime](https://get.dot.net/10)。

## 从源码构建

需要 .NET 10 SDK 与 Windows 环境：

```bash
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" \
  -c Release -p:Platform=x64 -o ./artifact --no-self-contained
```

### 可选：编译期密钥

自编译版本**不含任何内置密钥**，相关能力会降级但不影响启动游戏：

| 环境变量                             | 缺失时的表现                                                 |
| -------------------------------- | ------------------------------------------------------ |
| `PCL_CURSEFORGE_API_KEY`         | CurseForge 官方接口返回 403，自动回退 mcim 镜像（需把下载源设为「自动」或「优先镜像」） |
| `PCL_MS_CLIENT_ID`               | 微软正版登录不可用，仅能使用离线登录                                     |
| `PCL_NAID_CLIENT_ID` / `_SECRET` | Natayark ID 登录不可用（大厅已移除，通常不再需要）                        |



```bash
PCL_WRITE_SECRET=1 \
PCL_CURSEFORGE_API_KEY='$2a$10$...' \
PCL_MS_CLIENT_ID='xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx' \
  dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" \
  -c Release -p:Platform=x64 -o ./artifact --no-self-contained
```

注意 `$` 在 bash 双引号中会被展开，key 请用单引号包裹。

## 已知问题

- **更新服务默认指向一个已失效的地址**，启动器会跳过在线更新检查
- 节点优选首轮耗时较长（约 4–5 分钟，取决于网络），结果带 1 小时缓存
- 未内置 CurseForge key 时，搜索结果的排序与部分字段与官方略有差异

## 反馈

**本分支的问题不要提交给 PCL 或 PCL CE 上游** —— 上游无法复现这里的改动，提交过去只会增加他们的负担。

请在本仓库 [提交 Issue](https://github.com/Havesten/PCL-CE/issues/new/choose)，附上启动器日志（`PCL/Log/` 下对应文件）。

## 致谢与许可

这个分支能存在，完全依赖龙腾猫跃把 PCL 的源码开放出来，以及 PCL CE 社区持续的维护。如果它帮到了你，请赞助原作者：[afdian.com/a/LTCat](https://afdian.com/a/LTCat)。

许可说明：

- `Plain Craft Launcher 2/` 目录适用《PCL 分发有限许可》与《PCL 存储库合理使用指南》，全文见 [`Plain Craft Launcher 2/LICENCE`](Plain%20Craft%20Launcher%202/LICENCE)。本分支按其中「重度使用」条款处理：
  - 明确声明为第三方基于 PCL 独立进行的二次创作，非官方版本
  - 名称以 PCL 开头，并带表示第三方修改的后缀
  - 保留关于页首位的原作者署名与赞助入口，并公开本仓库源码
  - 保留 Minecraft 正版购买劝导（`McLaunchPrecheck`）与赞助劝导
  - 不提供任何与赞助解锁类似的功能，界面主色保持蓝色
- 其余目录适用 [Apache License 2.0](LICENSE)

本分支免费分发，不收取任何费用，也没有任何形式的付费版本或赞助门槛。

## Star 历史

<a href="https://www.star-history.com/?repos=Havesten%2FPCL-CE\&type=date\&legend=top-left">  
  <img src="https://api.star-history.com/chart?repos=Havesten/PCL-CE\&type=date\&legend=top-left" alt="Star History" width="600">  
</a>
