# 协议 v1 / 服务端 0.7.0

HTTP 和 WebSocket 共用一个 TCP 端口。状态接口 `GET /health`，WebSocket 路径 `/ws`；网页测试界面默认关闭，只有允许网页应用的自建环境才显式开启。UTF-8 JSON，入站单条消息上限 131072 字节；HTTP 请求体上限 4096 字节。

## 建立会话

正式登录使用 HTTPS（本机 loopback 测试可用 HTTP）：

- `POST /api/auth/register`：username、password、name，成功后再登录。
- `POST /api/auth/login`：username、password、remember（可选，默认 false）。
- `POST /api/auth/steam/begin`：name，返回 state、pollSecret、同源授权 url；浏览器进入 url 完成官方授权。
- `POST /api/auth/steam/poll`：state、pollSecret、remember；未完成返回 pending，成功只消费一次。
- 登录成功返回 token、playerId、name、provider、protocol，记住登录时另返回 refreshToken。
- `POST /api/auth/refresh`：refreshToken，恢复账号并返回新的 token / refreshToken。旧 refreshToken 同一事务内删除。
- `POST /api/auth/logout`：refreshToken、token，撤销该设备凭据及当前临时会话。
- `POST /api/auth/guest`：name，需服主显式开启 NIGHTSTUD_ALLOW_GUEST=1。
- `GET /api/auth/options`：accounts / steam / guest 功能状态。

refreshToken 为随机 selector.secret，数据库仅存 secret 的 SHA256，30 天有效。网络组件接收 refreshToken 后使用 Windows 当前用户 DPAPI 加密保存，临时 token 留在该组件内存。密码由 UI 组件经官方模组消息通道交给网络组件，发送后清空表单。provider 由服务端赋值，客户端不能声明 Steam 身份。

旧 `POST /api/sessions` 在 NIGHTSTUD_ALLOW_LEGACY_LOGIN=1 时开放：

```json
{"name":"店主甲","accessKey":"开服者设置的测试口令"}
```

成功响应包含 `token`、`playerId`、`name`、`protocol: 1`。令牌由密码学随机数生成，代表本次服务运行期间的测试身份。客户端保留令牌以便重连；令牌不放入 URL。

连接 `/ws` 后五秒内发送：

```json
{"type":"hello","protocol":1,"token":"上一步返回的令牌","games":["stud","three","holdem","blackjack"]}
```

服务端回复 `welcome`、`state` 和 `lobby`。`welcome.features` 给出服务功能；`games` 为客户端支持的玩法，旧客户端省略时只允许梭哈。同一令牌再次连接会替换旧连接。浏览器必须同源；原生游戏客户端可省略 Origin 头。

## 命令

账号扩展命令（仍需 id）：playerFilter(filter=all / steam / nonSteam)、privacy(allowVisits / allowInvites)、chat(text)、visit(target)、leaveVisit、shop(name / payload=JPEG base64 或空字符串)、tradeOffer(target / trade / price / payload)、tradeAccept(trade)、tradeCancel(trade)、tradeReceipt(trade)。

物品 payload 为限定 JSON，最多 64 个节点、60000 字符；禁用多态类型字段。服务端记录 proposed / committed / cancelled，核对双方账号和店内访问关系。买卖双方实际存档结算由客户端持久化账本按交易 ID 去重。tradeReceipt 可反复查询；重新登录后继续查询待完成记录。

串门、邀请权限在服务端执行。聊天分大厅 / 房间 / 店内频道；消息最多 120 字，默认每 10 秒最多 5 条，违禁推广与链接过滤，服主可热加载禁言 / 封禁 ID。发布店铺最多每 4 秒一次，JPEG 尺寸上限 960×540、80KB，每店最多 4 名访客。

变更命令都携带长度 1–64 的 `id`，可使用字母、数字、短横线和下划线。`ready`、`act`、`leave` 还需携带最新状态中的 `revision`。

```json
{"type":"create","id":"c1","ante":10,"buyIn":1000}
{"type":"create","id":"c2","ante":10,"buyIn":1000,"game":"holdem","public":true,"name":"今晚一桌"}
{"type":"join","id":"j1","code":"8位房间码"}
{"type":"ready","id":"r1","revision":2,"ready":true}
{"type":"act","id":"a1","revision":4,"move":"bet","amount":25}
{"type":"leave","id":"l1","revision":5}
{"type":"invite","id":"i1","target":"大厅中的玩家 ID"}
{"type":"acceptInvite","id":"i2","invitation":"本人收到的邀请 ID"}
{"type":"declineInvite","id":"i3","invitation":"本人收到的邀请 ID"}
{"type":"sync"}
{"type":"ping"}
```

`game` 为 `stud`、`three`、`holdem`、`blackjack`，省略时为梭哈；`public` 默认 false。`move` 使用当前 `legalMoves` 中的值：扑克包含 `check`、`bet`、`call`、`raise`、`fold`、`allIn`，炸金花另有 `showdown`；看自己的牌属于客户端显示操作。二十一点使用 `hit`、`stand`。`amount` 用于 `bet` 和 `raise`，表示本次从剩余筹码中实际投入的总额，含跟注部分；省略时采用当前最低金额。服务器的 `legalMoves` 给出允许动作、所需金额 `amount` 和可填金额上限 `max`。

客户端约每 15 秒发送 `ping`，回复为 `pong`。`sync` 返回最新状态。每个会话每秒最多 20 条命令；登录每 IP 每分钟最多 20 次，WebSocket 每 IP 每分钟最多 30 次。

变更命令回复 `ack`，随后返回当前状态：

```json
{"type":"ack","id":"a1","ok":true}
{"type":"ack","id":"a2","ok":false,"error":"stale","message":"牌桌已更新，请按当前牌面操作。"}
```

服务端保存每个会话最近 128 个变更命令编号以拒绝重复操作。过期版本、越权座位、错误金额、未到行动顺序和房间满员分别拒绝。重新发送前先读取最新状态，并生成新的命令编号。

## 按座位生成状态

`state.room` 为 `null` 时位于大厅；入桌后包含：

| 字段 | 含义 |
| --- | --- |
| `code`, `revision`, `ownSeat` | 房间码、状态版本和本人座位（0 或 1） |
| `game`, `name`, `isPublic` | 玩法代号、房间名称、是否公开 |
| `board`, `dealerSeat` | 德州公共牌 ID 数组；二十一点庄家座位，其余玩法为 -1 |
| `ante`, `buyIn`, `unit` | 底注、约定带入额、本轮最低下注 |
| `phase` | `waiting`、`playing`、`paused`、`finished` |
| `hand`, `round`, `actor` | 第几手、下注轮次、当前行动座位；结束后 actor 为 -1 |
| `pot` | 当前底池 |
| `players` | 两个固定座位，空座为 null |
| `legalMoves` | 当前本人可做的操作，非本人回合或暂停时为空 |
| `result` | 本手结果：赢家座位、底池、是否摊牌、双方净赢亏；平局赢家为 -1 |
| `events` | 公开动作记录：发牌、行动和离桌等 |

每个玩家对象包含 ID、姓名、座位、剩余筹码、准备状态、在线状态、重连截止时间、本手累计投入、本轮投入和 `cards`。牌 ID 为 `花色 × 13 + 点数序号`；花色 0–3 为梅花、方块、红桃、黑桃，点数序号 0–12 对应 2–A。

梭哈牌数组第一张是暗牌，对手暗牌在摊牌前为 `null`。炸金花、德州对手底牌在摊牌前均隐藏；二十一点庄家暗牌在闲家停牌前隐藏。自己的牌完整可见。弃牌结束时保持对手暗牌隐藏。状态中不传输牌堆、未发的牌或会话令牌。

## 大厅与邀请

`lobby` 帧包含 `selfId`、`features`、`totalOnline`、`players`、`rooms`、`invitations`。

- `players`：最多 100 位在线玩家，字段为 `id`、`name`、`status`（`lobby`、`room`、`playing`）。
- `rooms`：最多 64 个公开房间，字段为 `code`、`name`、`game`、`seats`、`capacity`、`playing`、`ante`、`buyIn`。私人房间不会列入。
- `invitations`：只发给收件人，最多 8 条，字段为 `id`、`fromId`、`fromName`、`code`、`game`、`name`、`expiresAt`。

邀请有效期两分钟，同一发送者与目标之间至少间隔十秒；接受时再次验证收件人、房间和空位。列表在连接、入退房、准备、结算和邀请变更后推送。使用房间码和邀请均需先登录。

协议编号仍为 1，新增字段采用可选扩展。第三方客户端应依据 `features` 和 `legalMoves` 展示入口及操作。

## 0.5.0 信用点与实物

客户端 hello 可声明 `capabilities:["storeStakes"]`。服务端 `features` 增加 `storeStakes`、`goodsStakes`。访客和未声明能力的客户端只使用练习筹码。

创建命令可带 `economy:"practice"|"store"`，默认为练习。信用点桌的新座位余额为零；`fund` 通过当前账号、`saveId` 的暂存余额带入房间金额，成功后才允许 `ready`。桌内操作附带原存档 `saveId`。`tableId` 为本次服务器牌桌账本标识，区别于可以重复生成的房间码。

`cashWallet` 查询余额。`cashTransfer` 带 32 位小写十六进制 `transfer`、`saveId`、`direction:"in"|"out"`、`amount`（1–1000000）。`cashReceipt` 查询凭据；缺失时须先用 `cashCancel` 持久取消，再处理本地返还。出账已预留，客户端保存后发 `cashDelivered`。相同编号、账号、存档、方向和金额重试不重复改动余额。

服务端 `cash` 帧含 `request`、`transfer`、`saveId`、`available`、`receipt:{id,direction,state,amount}`。状态为入账 `accepted`、出账 `reserved`/`delivered` 或 `cancelled`。客户端只处理当前服务器、账号、经营存档中已有的对应暂存日志。

双人信用点桌在开局前可用 `goodsOffer`：`transfer`、`saveId`、`payload`（明确字段的本地原版物品文档）、正数 `price`（显示估价，实物不折算信用点）。每人每手一件。接收端通过 `goodsReceipt` 获取并用自己的游戏还原、校验，然后用 `goodsApprove` 同意。全部押品确认后才可发牌，开局锁定。`goodsCancel` 在未锁定时撤回，支持缺失记录的取消凭据。

状态 `room.goods` 只传物品编号、主人、手数、座位、名称、估价、原版图标和确认状态；完整文档按件查询。`goodsList` 查询当前账号与存档的待领编号。`goods` 凭据帧包含 `id`、`saveId`、`owner`、`state`、`payload`：`offered`、`accepted`、`locked`、`claimable`、`delivered`、`cancelled`、`lost` 或 `missing`。获胜交付和平局退回均以 `claimable` 给对应收件人；保存本地领取结果后发 `goodsDelivered`，服务端清除大文档但保留凭据。

`ready`、`fund`、`goodsOffer`、`goodsApprove` 依旧检查牌桌修订号。现金与实物结算在同一 SQLite 事务内；服务器重启作废未结束那手，保留此前已完成的结果。

## 0.6.0：每轮抵注与多人押品

`hello.capabilities` 增加 `roundGoods`；全桌有此能力时才开启每轮抵注或多人押品。`features` 增加 `roundGoods` 和 `groupGoods`。原有双人开局押品保持兼容；每手可有多件，账号在途和待领押品仍有数量上限。

梭哈、炸金花、德州的 `room.assetRules` 提供当前行动人的 `actor,due,minimumBet,minimumRaise,cash,other`。客户端用共享 `AssetBetRules.Plan` 计算整件实物抵注后的 `move,amount,cash`，在扣留物品前向玩家展示；物品价值高于对方能匹配的金额时不接受。牌局中的 `goodsOffer` 必须附 `wager:true,move,amount`，并携带既有的修订号、存档、文档和估价。服务端复算计划，金额变化时拒绝，不擅自补扣更多信用点。

服务端持久化当前手数、轮次、物品、行动及总投入。`room.assetPending` 含提案 ID、座位和 16 秒到期时间；核对时牌局不接受正常下注。接收端成功还原并校验物品后自动 `goodsApprove`；无法还原时 `goodsReject`。超时或拒绝退回原主，保持行动次序。锁定后重复确认不再次执行下注。

`room.pot` 为钱货总值，`room.assetValue` 为本手已抵注的实物估价，`room.creditPot` 为其中信用点。结算从数值底池中剥离实物估价，把实际信用点和物品归属放进同一事务；`result.net` 和 `players.stack` 均只计信用点。`result` 另有 `creditPot,assetValue`。原有开局押品属于额外约定，未抵作信用点。

多人开局押品要求所有其他参与者核对。`acceptedBy` 为兼容字符串：多个已核对账号用逗号连接。获胜玩家收回自己的物品，其余物品由服务端按估价从高到低，分给已分得最少的获胜玩家；流局退回。客户端通过同一份 `goodsList`/`goodsReceipt`/`goodsDelivered` 流程收取，物品不拆分或复制。

## 0.6.0：共享店铺后端

`sharedShop` 能力协商后使用 `shopScene` 发布原版白名单素材、几何和公开货物元数据；服务端校验结构并分配场景修订号与访客位置。`shopEnter` 只在店主发布的门已完全打开时生效，进入的是顾客区；`shopExit,shopLook,shopKick` 分别处理退回、白名单朝向和店主请离。账号 `appearance` 使用固定外观 ID。客户端反向场景仍在审稿，此接口不代表双视角画面已完成。

`shopInspect,shopAsk,shopAnswer` 使用场景修订号、公开物品的不透明 ID 和服务端询价凭据；报价须绑定同一访客及相同种类、数量和状态。`chat.channel` 可附期望的 `visit:<hostId>`，服务端场合已变时拒绝发送，避免私聊误入公共大厅。旧截图串门保留给未协商 `sharedShop` 的客户端。

## 0.6.1：柜台接待、询价与取消

`ShopScene.counterBusy` 表示店主的原版营业顾客正在占用接待窗口。客户端只发布布尔值，顾客身份和剧情不入协议。忙碌期间 `shopInspect` 继续可用，`shopAsk` 返回 `counter_busy`；恢复空闲后可再次问价。忙碌、换货、离店或到期会结束已有询价。

`shopInquiry` 与 `shopAsked` 新增 `host,epoch`。`shopAnswer` 向原店主和原询价者发送 `host,id,item,status,message`，状态为 `declined,changed,expired,quoted,left,closed,busy`。接收端仅清除同店同编号的草稿与等待状态。45 秒本地超时作为旧服务端兼容处理。

成功 `tradeOffer` 消耗其绑定的询价；换交易编号重用该询价被拒绝，重发同一交易编号仍返回已保存收据。`tradeCancel` 在写入取消状态后立即通知双方。`leaveVisit` 事务取消离店者与店主之间 `proposed` 交易，保留 `committed` 及其他访客的交易。取消收据仍只包含 `type,id,state`；钱货返还由各自已有的本地暂存账本执行。

## 0.6.2：接待对象

连接协商 `shopReception`。店主启用后，公共 `shopScene` 带 `reception:true,serving,nextVisitor`，分别表示启用此规则、当前接待账号、预定访客。原版顾客占用窗口时 `serving` 为空，空闲后恢复预定访客。`people.serving` 唯一标记当前访客，`queuePosition` 为 0 表示接待中，1 起表示等候顺序。排序依据到店时间，复用槽号不会改变先后。

店主的 `shopServe` 携带 `target,sceneRevision,shopPresence,shopEpoch`。服务器校验同店身份、布景、人员快照、接待会话与频率。营业期间选择只预定下一位。客户端收到确认后的场景才换人物，成功 ACK 不单独改变立绘；同店同会话的旧快照丢弃。

等候访客可询价并收到排队状态；绑定询价的报价要求该访客正在被接待。已有交易编号继续从持久收据恢复。未协商接待能力的店主保留原有串门流程。

## 0.6.3：访客卖货与成交检查

访客向店主发送原有 `tradeOffer`，店主用 `tradeAccept` 收购或 `tradeCancel` 拒绝。在 `shopReception` 店铺内，所有新成交都要求当前访客正在被接待，原版营业顾客占用时拒绝成交。覆盖店主买入和卖出两个方向；重复查询已成交收据保持可用，不因随后切换访客而取消已完成交易。

客户端新交易日志保存 `server,account,save,target`，恢复和应用收据前核对归属，已成交帧还须匹配双方身份与价格。取消凭据不携带私人物品和身份，依靠原有本地日志恢复。遇到旧服务器的 `missing` 先请求持久取消，收到取消凭据后再返还。服务器拒绝确认时，客户端请求撤回，按终态收据恢复。

未记录服务器和存档字段的旧客户端日志保留为待恢复记录，用户连接原服务器后在旧版恢复页选择绑定；写盘失败恢复原记录。此信息无法从旧记录中自动推断。服务器不能证明离线存档修改或复制所产生的物品来源。


## 0.6.4：开门请求

联机组件握手声明 `shopBell`。服务端仅向支持者的 welcome 和大厅目录暴露该功能；店铺场景的 `doorbell` 表示店主支持接收提醒。旧客户端继续使用既有串门流程。

访客发送 `shopBell`，携带当前 `sceneRevision,shopEpoch`。服务器从到访关系确定店主，忽略命令中伪造的目标；要求账号仍在这家店的柜台外、双方支持功能、店门关闭、版本和会话一致。每家店 8 秒内最多接受一次；同一访客的动作限流为 30 秒。通用命令编号去重继续生效。

通过后，店主收到 `shopBell`（`id,host,epoch,visitor,name`），访客收到 `shopBellSent`（同一 `id,host,epoch` 和 `cooldown:30`）。两种通知仅发给双方，门、站位和信用点不变。普通成功 ACK 仅确认命令处理，客户端等待实际通知回执后显示已通知。店主开门后继续由访客发送原有 `shopEnter`。

日志事件为 `shop_door_requested`，沿用原有审计字段。请求不写入交易账本，不修改商品和信用点。常见拒绝包括 `visit_scope,client_update,scene_changed,door_open,shop_bell_wait,shop_action_rate,visit_unavailable`。

## 0.6.5：陈列与内外柜台

新增全局 `shopDisplay` 服务能力，协议版本仍为 1。`ShopScene` 可含三块区域：`showcase`、`counter`（顾客外侧）、`innerCounter`（店主里侧）；最多 192 件、96,000 字符。客户端从实际库存模型按固定原版像素发布，与隐藏面板、屏幕缩放无关。

`ShopThing` 新增 `canTrade`（旧帧缺省 true）、`orientation`（0..3，缺省 0）、`flipped`（缺省 false）。陈列目录只提供原版素材元数据；`canTrade=true` 仍需既有 GoodsLedger 白名单和正估价。`shopInspect` 可看陈列品；`shopAsk` 对只读陈列返回 `display_only`。陈列品不会进入交易账本。变成只读的货会使旧询价失效。

客户端遇旧服务器会过滤里侧柜台和只读物品，保留原两区、48 件上限。新服务器接受旧帧。当前版本同步原版物品种类、数量、标签、格位和旋转；新增模组物品及任意自定义精灵不进入展示目录。原有每店发布频率及到访身份、隐私、范围验证保留。

内外柜台的报价复用 `tradeOffer` / `tradeAccept` / `tradeCancel` / `tradeReceipt`，不新增绕过结算的拖放命令。买卖双方各自在自己的存档持久暂存，再通过成交收据交付；只读预览不提供原生可取走的库存副本。

## 0.6.6：待报价柜台

`shopCounter` 全局能力与 `scene.counterStaging=true` 同时出现才启用。新字段缺省 false，旧店主保留原选货路径。`shopScene` 附带独立 `counterRevision`、`counterLots`；柜台变化不修改原货物库存，款项和实物继续通过持久交易处理。

`shopCounterPut`：带当前 `shopEpoch`、`sceneRevision`、`counterRevision`、随机 `counterLot`、`counterItem`、参考估价 `price` 和通过 GoodsLedger 验证的 `payload`。仅当前到访本人及接待中的访客可上货；核对根物品种类/数量、实际原版形状、格位、碰撞及正估价。一个访客一件，全局资料缓存最多 2,000,000 个字符，120 秒租期。公开帧仅含种类、数量、公开标签与格位。

`shopCounterRemove`：凭本人当前 `counterLot` 与 `counterRevision` 取回展示，旧请求不会移走别人的货。`shopCounterKeep`：每 10 秒最多一次，客户端常规 30 秒续期。营业接管、换人、断线、离店、改变店铺及过期会清台。

新柜台的 `tradeOffer` 可带 `counterLot` 与 `counterHash`（UTF8 资料 SHA256 的 Base64），省略重复的 `payload`。服务器取回当时验证的原资料，再按原有交易流程保存报价；资料改变或租期失效会拒绝。断线恢复按原交易编号查询，已存在收据先于柜台及到访检查处理；恢复请求可携带原持久暂存的完整资料。

单条传入 WebSocket 上限 131,072 字节、客户端接收 196,608 字节不变。新发物品预检 JSON 字符串的传入与返回编码预算，服务器再次核对返回预算。客户端旧寄存解码不套用新增发送预算，防止更新阻断既有恢复。没有新增数据库表或公网页测试入口。


## 0.7.1：斗地主主动短句

`tableVoice` 能力允许 `tablePhrase` 的 `move` 为 `say:wait`、`say:coffee`、`say:nice`、`say:partner`、`say:show`、`say:seventeen`、`say:ready`。发送者必须已入当前正在进行的斗地主房间，code 和 revision 一致；可在非本人回合发送。全桌 8 秒冷却，自动 wait 仍验证当前行动者、20 秒静置和 35 秒冷却。

广播 tablePhrase 包含 id、code、hand、phrase、speaker（服务端确定的座位）、manual。不改变牌局 revision。只广播给本桌在线席位。客户端按同一 speaker 选择音色，不随 ownSeat 改变。任意文本、文件、路径均拒绝。旧服没有该能力时，客户端显示更新提示。


## 0.7.2：按玩法提供快捷语，3秒冷却

新能力 `tableVoiceAllModes` 表示手动短句适用于七种牌局，且全桌冷却为3秒。保留 `tableVoice` 兼容原斗地主入口。自动催牌仍只用于斗地主。

斗地主可用 wait/coffee/nice/partner/show/seventeen/ready；stud、three、holdem、blackjack、mahjong 可用 wait/nice/show/ready；dice 只可用 wait/show。不符合当前玩法的代号返回 bad_phrase。消息与响应字段不变；发话座位来自服务端，不能由客户端指定。
