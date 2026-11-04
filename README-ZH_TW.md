[简体中文](README.md) | [English](README-EN.md) | **繁體中文**

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.ico" alt="Logo" width="80" height="80">

# PCL CE FIX

基於 PCL 社群版二次開發的第三方分支

[本分支發佈頁](https://github.com/Havesten/PCL-CE/releases) |
[社群版上游](https://github.com/PCL-Community/PCL-CE) |
[PCL 原作者](https://github.com/Meloong-Git/PCL)

[提交問題](https://github.com/Havesten/PCL-CE/issues/new/choose) |
[更新日誌](https://github.com/Havesten/PCL-CE/releases)

</div>

## 這是什麼

PCL（Plain Craft Launcher）是龍騰貓躍開發的 Minecraft 啟動器。PCL CE 是社群在其開放原始碼的基礎上繼續開發的版本。

本儲存庫是 **PCL CE 的第三方分支**，由個人獨立維護。它不是 PCL 或 PCL CE 的官方版本，也不代表上游的任何立場。

## 與原版 PCL CE 的差異

### 新增：下載節點優選

原本只能在「官方源」和「鏡像源」之間二選一，沒法控制具體連哪台節點。現在會先對每台候選節點做 TCP 與 TLS 握手計時，多輪取中位數抑制抖動，鎖定延遲最低的一個，後續請求直接連它 —— URL、SNI、憑證校驗全部保持不變。

涵蓋 16 個網域，分四組：

| 分組 | 網域 |
|---|---|
| Mojang 本體 | piston-meta、launchermeta、launcher、resources 等 6 個 |
| 模組載入器 | Forge、NeoForge、Fabric 的 maven 與列表站，共 5 個 |
| CurseForge | api、edge、mediafilez |
| Modrinth | api、cdn |

Cloudflare 站點會額外改用通用優選 IP 池（`cloudflare.182682.xyz` 的解析結果），整個池只測速一次，所有 CF 網域共用結果 —— 因為 CF 是 Anycast，一組優選 IP 對所有接入 CF 的網域通用。非 CF 站點（AWS CloudFront、Fastly、Azure）判定後照常走自己的解析結果，不會誤用。

入口在「設定 → 下載 → 節點優選」，可隨時重測或固定節點。

### 調整：更新源

- 移除 Mirror 醬內測通道及對應的 CDK 輸入框
- 更新位址改為私有伺服器，協議與 Minio 源相容
- 關閉線上更新時不再彈社群版公告

### 刪除重構：連線部分

原「大廳」入口及其依賴的 Natayark 帳號登入已移除，連線整體刪除後重建：從「工具」頁提到標題列，成為與啟動、下載、設定、工具並列的入口，目前對外提供中繼連線。

- **中繼連線**：房主本地埠由中繼伺服器映射到公網，不需要公網 IP，雙方都在 NAT 後面也能連。需要信箱驗證碼登入（首次登入即註冊，帶人機驗證），登入後可看月流量、限速與埠名額；這些限制由伺服器下發並在伺服器端執行，改本機設定無法繞過。通道協定為自研 PTP v1，用戶端與伺服器全部內建，不需要任何外部執行檔
- **P2P 直連已撤下**：頁面實作保留在 `Pages/PageLink/PageLinkP2P`，導覽裡不再出現——純直連要求至少一方具備公網 IP，雙方都在 NAT 後面就必然連不上，作為預設入口體驗太差。需要時在 `PageLinkLeft.xaml` 取消 `ItemP2P` 註解即可恢復

### 變更：帳號

- 新建檔案只提供「微軟帳號」與「第三方驗證」兩種方式，**離線登入入口已移除**。已存在的離線檔案不受影響，仍可正常啟動

### 調整：介面

- 移除首頁右側常駐的社群版提示卡片
- 標題列 Logo 後加 `FIX` 標識，便於和上游建置區分
- 預設遊戲視窗標題改為 `玩家：{user} 登入方式：{login} 版本：{version} 時間：{date} {time}`
- 「工具」頁裡的連線入口已移除，連線統一走頂欄的「連線」頁
- P2P 連線頁暫時隱藏：導覽裡只留「中繼連線」，進連線頁直接落在中繼面板；P2P 頁程式碼與其房間碼 / 對端位址 / 協定堆疊三選一邏輯都原樣保留，恢復只需在 `PageLinkLeft.xaml` 裡取消 `ItemP2P` 的註解
- 「設定 → 連線（EasyTier）」整頁刪除：使用者名稱、協定偏好、延遲優先、對稱 NAT 埠猜測、Cli 偵錯輸出這些選項只在 EasyTier 直連時生效，直連入口隱藏後已無處可達，連「工具」分類與「隱藏功能」裡對應的勾選框一併移除

## 功能

以下均為上游已有能力，本分支原樣保留：

- 版本安裝、整合包匯入、模組 / 資源包 / 存檔管理
- 模組與資源下載（CurseForge、Modrinth）
- **多人連線**：EasyTier 通道、公共中繼節點與自建中繼伺服器設定（本分支在此基礎上重構，見上文）
- 微軟正版登入、皮膚與披風管理、多帳號切換
- Java 自動偵測與安裝、崩潰分析、日誌匯出

## 下載

從 [Releases](https://github.com/Havesten/PCL-CE/releases/latest) 取最新建置，單一 exe，解壓即用，無安裝程式。

環境要求與上游一致：Windows 10 1809 (17763) 及以上，需安裝 [.NET 10 Desktop Runtime](https://get.dot.net/10)。

## 從原始碼建置

需要 .NET 10 SDK 與 Windows 環境：

```bash
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" \
  -c Release -p:Platform=x64 -o ./artifact --no-self-contained
```

### 選用：編譯期金鑰

自編譯版本**不含任何內建金鑰**，相關能力會降級但不影響啟動遊戲：

| 環境變數 | 缺少時的表現 |
|---|---|
| `PCL_CURSEFORGE_API_KEY` | CurseForge 官方介面回傳 403，自動回退 mcim 鏡像（需把下載源設為「自動」或「優先鏡像」） |
| `PCL_MS_CLIENT_ID` | 微軟正版登入不可用，僅能使用離線登入 |
| `PCL_NAID_CLIENT_ID` / `_SECRET` | Natayark ID 登入不可用（大廳已移除，通常不再需要） |

```bash
PCL_WRITE_SECRET=1 \
PCL_CURSEFORGE_API_KEY='$2a$10$...' \
PCL_MS_CLIENT_ID='xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx' \
  dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" \
  -c Release -p:Platform=x64 -o ./artifact --no-self-contained
```

注意 `$` 在 bash 雙引號中會被展開，金鑰請用單引號包住。

## 已知問題

- **更新服務預設指向一個已失效的位址**，啟動器會跳過線上更新檢查
- 節點優選首輪耗時較長（約 4–5 分鐘，取決於網路），結果帶 1 小時快取
- 未內建 CurseForge 金鑰時，搜尋結果的排序與部分欄位與官方略有差異

## 回饋

**本分支的問題不要提交給 PCL 或 PCL CE 上游** —— 上游無法重現這裡的改動，提交過去只會增加他們的負擔。

請在本儲存庫 [提交 Issue](https://github.com/Havesten/PCL-CE/issues/new/choose)，附上啟動器日誌（`PCL/Log/` 下對應檔案）。

## 致謝與授權

這個分支能存在，完全依賴龍騰貓躍把 PCL 的原始碼開放出來，以及 PCL CE 社群持續的維護。如果它幫到了你，請贊助原作者：[afdian.com/a/LTCat](https://afdian.com/a/LTCat)。

授權說明：

- `Plain Craft Launcher 2/` 目錄適用《PCL 分發有限許可》與《PCL 儲存庫合理使用指南》，全文見 [`Plain Craft Launcher 2/LICENCE`](Plain%20Craft%20Launcher%202/LICENCE)。本分支按其中「重度使用」條款處理：
  - 明確聲明為第三方基於 PCL 獨立進行的二次創作，非官方版本
  - 名稱以 PCL 開頭，並帶表示第三方修改的後綴
  - 保留關於頁首位的原作者署名與贊助入口，並公開本儲存庫原始碼
  - 保留 Minecraft 正版購買勸導（`McLaunchPrecheck`）與贊助勸導
  - 不提供任何與贊助解鎖類似的功能，介面主色保持藍色
- 其餘目錄適用 [Apache License 2.0](LICENSE)

本分支免費分發，不收取任何費用，也沒有任何形式的付費版本或贊助門檻。

## Star 歷史

<a href="https://www.star-history.com/?repos=Havesten%2FPCL-CE&type=date&legend=top-left">
  <img src="https://api.star-history.com/chart?repos=Havesten/PCL-CE&type=date&legend=top-left" alt="Star History" width="600">
</a>
