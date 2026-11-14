"use strict";
const $=id=>document.getElementById(id);
const labels={check:"过牌",bet:"下注",call:"跟注",raise:"加注",fold:"弃牌",allIn:"全押",showdown:"比牌",hit:"要牌",stand:"停牌",bid:"叫地主",decline:"不叫",play:"出牌",pass:"过",discard:"打出选中牌",pung:"碰",chow:"吃",kong:"杠",win:"胡",selfWin:"自摸",odd:"猜单",even:"猜双",reveal:"开盅"};
const games={landlord:"三人斗地主",mahjong:"货栈麻将",dice:"骰盅",stud:"五张梭哈",three:"炸金花",holdem:"德州扑克 · 底注桌",blackjack:"二十一点 · 轮庄桌"};
const rules={landlord:"固定底分，地主两份、农民各一份；炸弹与春天不翻倍。点牌选中，再出牌。",mahjong:"四组一对、七对或十三幺可胡。吃上家、碰杠三家，胡优先；1 番为底分，逐番翻倍、5 番封顶。点炮者付、自摸三家各付，支持一炮多响。",dice:"闲家猜三颗骰子总和的单或双，双方各押底注，庄家开盅。猜中收双方押注，每手交换庄家。",stud:"52 张牌，一暗四明，四轮下注。跟到底比五张牌，或让对方弃牌收池。",three:"双方底注，各三张暗牌；三轮跟齐或主动比牌。看牌不改注额。三条＞同花顺＞同花＞顺子＞对子＞散牌，A23 最小顺子。",holdem:"双方底注，各两张底牌；公共牌按三、一、一张发出，四轮下注。七张中取最大的五张比较。",blackjack:"每手轮庄，闲家要牌或停牌，庄家未满 17 补牌、软 17 停牌。天然 21 按 3:2 赔付，零头取整，普通获胜 1:1；庄家预留 1.5 倍赌注，平局退回原押。"};
let directory=null,features=[];
let socket,token=sessionStorage.getItem("nightstud-token"),state=null,pending=false,retry=null,manual=false;
const message=text=>{$("message").textContent=text;};
const status=text=>{$("status").textContent=text;};
const clear=node=>node.replaceChildren();
function send(type,extra={}){
  if(socket?.readyState!==WebSocket.OPEN){message("连接尚未恢复。");return;}
  const cmd={type,id:crypto.randomUUID?.()||`${Date.now()}-${Math.floor(Math.random()*1e8)}`,revision:state?.revision,...extra};
  if(type!=="sync"&&type!=="ping")pending=true;
  socket.send(JSON.stringify(cmd));render();
}
function connect(){
  if(!token)return;
  manual=false;status("连接中…");
  socket=new WebSocket(`${location.protocol==="https:"?"wss:":"ws:"}//${location.host}/ws`);
  socket.addEventListener("open",()=>socket.send(JSON.stringify({type:"hello",token,protocol:1,games:Object.keys(games)})));
  socket.addEventListener("message",event=>{
    const msg=JSON.parse(event.data);
    if(msg.type==="welcome"){status(`已连接 · ${msg.name}`);$("login").hidden=true;$("logout").hidden=false;message("");features=msg.features||[];for(const option of $("game").options)option.disabled=option.value!=="stud"&&!features.includes(option.value);}
    if(msg.type==="chat"&&msg.entry){const entry=document.createElement("p");entry.textContent=msg.entry.name+"："+msg.entry.text;$("chat-history").append(entry);while($("chat-history").children.length>32)$("chat-history").firstElementChild.remove();}
    if(msg.type==="lobby"){directory=msg;renderDirectory();}
    if(msg.type==="state"){state=msg.room;render();}
    if(msg.type==="ack"){pending=false;if(!msg.ok)message(msg.message);else message("");render();}
    if(msg.type==="error"){
      message(msg.message);
      if(msg.error==="unauthorized"||msg.error==="protocol"){
        manual=true;token=null;sessionStorage.removeItem("nightstud-token");$("login").hidden=false;
      }
    }
  });
  socket.addEventListener("close",()=>{
    pending=false;status("连接断开 · 正在重连");render();
    if(!manual)retry=setTimeout(connect,2000);else status("请重新连接");
  });
  socket.addEventListener("error",()=>message("连接服务失败，请检查地址和服务状态。"));
}
$("login-form").addEventListener("submit",async event=>{
  event.preventDefault();
  try{
    requireSecure();
    const response=await fetch("/api/auth/login",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({username:$("username").value,password:$("password").value})});
    if(!response.ok){let error;try{error=await response.json();}catch{}throw Error(error?.message||`连接失败（${response.status}）`);}
    const data=await response.json();token=data.token;sessionStorage.setItem("nightstud-token",token);$("password").value="";clearTimeout(retry);connect();
  }catch(error){message(error.message);}
});
function requireSecure(){if(location.protocol!=="https:"&&!["localhost","127.0.0.1","[::1]"].includes(location.hostname))throw Error("账号登录需要 HTTPS，请使用服主提供的 HTTPS 地址。");}
async function authPost(path,body){requireSecure();const response=await fetch(path,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(body)});const data=await response.json();if(!response.ok)throw Error(data.message||"登录失败，请稍后重试。");return data;}
function authenticated(data){token=data.token;sessionStorage.setItem("nightstud-token",token);$("password").value="";clearTimeout(retry);connect();}
$("register").addEventListener("click",async()=>{try{await authPost("/api/auth/register",{username:$("username").value,password:$("password").value,name:$("name").value});$("password").value="";message("注册成功，请输入密码登录。");}catch(error){message(error.message);}});
$("guest-login").addEventListener("click",async()=>{try{const response=await fetch("/api/auth/guest",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({name:$("name").value})});const data=await response.json();if(!response.ok)throw Error(data.message);authenticated(data);}catch(error){message(error.message);}});
$("steam-login").addEventListener("click",async()=>{
  let popup;try{
    requireSecure();popup=window.open("about:blank","_blank","popup,width=900,height=700");if(popup)popup.opener=null;
    const flow=await authPost("/api/auth/steam/begin",{name:$("name").value});const url=new URL(flow.url);
    if(url.origin!==location.origin||url.protocol!=="https:"||url.pathname!=="/auth/steam/start")throw Error("Steam 授权地址无效。");
    if(!popup)throw Error("浏览器拦截了授权窗口，请允许此站点弹出窗口后重试。");popup.location.href=url.href;message("请在 Steam 授权页确认登录。");
    const deadline=Date.now()+180000;
    while(Date.now()<deadline){await new Promise(resolve=>setTimeout(resolve,2500));if(popup.closed)throw Error("Steam 授权窗口已关闭。请重试登录。");const data=await authPost("/api/auth/steam/poll",{state:flow.state,pollSecret:flow.pollSecret});if(data.token){popup.close();authenticated(data);return;}}
    throw Error("Steam 登录已过期，请重新点击登录。");
  }catch(error){popup?.close();message(error.message);}
});
fetch("/api/auth/options").then(r=>r.json()).then(options=>{
  $("server-build").textContent="可能是赌的 / SERVER "+String(options.version||"");
  const secure=location.protocol==="https:"||["localhost","127.0.0.1","[::1]"].includes(location.hostname);
  $("guest-login").hidden=!options.guest;$("steam-login").disabled=!options.steam||!secure;
  $("login-submit").disabled=!secure;$("register").disabled=!secure;$("password").disabled=!secure;
  $("auth-status").textContent=!secure?"服务已启动。使用 HTTPS 地址后可注册、登录账号。":options.steam?"账号与 Steam 登录已就绪。":"账号登录已就绪，Steam 授权入口待配置。";
  if(!secure)$("password").placeholder="HTTPS 就绪后填写";
}).catch(()=>message("暂时无法读取登录配置，请检查连接。"));
$("player-filter").addEventListener("change",()=>send("playerFilter",{filter:$("player-filter").value}));
for(const id of ["allow-visits","allow-invites"])$(id).addEventListener("change",()=>send("privacy",{allowVisits:$("allow-visits").checked,allowInvites:$("allow-invites").checked}));
$("chat-form").addEventListener("submit",event=>{event.preventDefault();send("chat",{text:$("chat-text").value});$("chat-text").value="";});
$("logout").addEventListener("click",async()=>{try{await authPost("/api/auth/logout",{refreshToken:"",token});}catch(error){message(error.message);}manual=true;socket?.close();clearTimeout(retry);token=null;state=directory=null;sessionStorage.removeItem("nightstud-token");$("login").hidden=false;$("logout").hidden=true;render();});
$("create-form").addEventListener("submit",event=>{event.preventDefault();send("create",{ante:Number($("ante").value),buyIn:Number($("buy-in").value),game:$("game").value,public:$("public").checked,name:$("room-name").value.trim()});});
$("join-form").addEventListener("submit",event=>{event.preventDefault();send("join",{code:$("room-code").value.trim()});});
$("leave").addEventListener("click",()=>{if(state?.phase==="playing"||state?.phase==="paused"){if(!confirm("离开本手按弃牌结算，确定离桌？"))return;}send("leave");});
$("copy-code").addEventListener("click",async()=>{try{await navigator.clipboard.writeText(state.code);message("房间码已复制。");}catch{message(`房间码：${state.code}`);}});
function cards(node,values){
  clear(node);
  for(const value of values){
    const card=document.createElement("div");card.className="card";
    if(value===52||value===53){card.textContent=value===52?"小王":"大王";}
    else if(value===null){card.classList.add("back");card.textContent="♠";card.title="对手暗牌";}
    else{
      const suit=Math.floor(value/13),rank=["2","3","4","5","6","7","8","9","10","J","Q","K","A"][value%13],symbol=["♣","♦","♥","♠"][suit];
      if(suit===1||suit===2)card.classList.add("red");
      for(const [className,text] of [["rank",rank],["suit",symbol],["corner",rank+symbol]]){const span=document.createElement("span");span.className=className;span.textContent=text;card.append(span);}
      card.title=["梅花","方块","红桃","黑桃"][suit]+rank;
    }
    node.append(card);
  }
}
function button(text,action,gold=false){const node=document.createElement("button");node.textContent=text;node.disabled=pending||socket?.readyState!==WebSocket.OPEN;if(gold)node.className="gold";node.addEventListener("click",action);$("actions").append(node);}
$("amount").addEventListener("input",()=>{const node=$("actions").querySelector("[data-wager]");if(node)node.textContent=`${labels[node.dataset.wager]} ${$("amount").value}`;});
function render(){
  renderDirectory();
  $("lobby").hidden=!token||!!state||$("login").hidden===false;$("table").hidden=!state;
  for(const form of [$("create-form"),$("join-form")])for(const node of form.elements)node.disabled=pending||socket?.readyState!==WebSocket.OPEN;
  if(!state)return;
  if(["landlord","mahjong","dice"].includes(state.game)){renderGroup();return;}
  $("code").textContent=state.code;$("round").textContent=`${games[state.game||"stud"]} · 第 ${state.hand} 手${state.game==="blackjack"?" · 轮庄":` · 第 ${state.round} / ${state.game==="three"?3:4} 轮`}`;
  $("mode-rules").textContent=rules[state.game||"stud"];
  const own=state.players[state.ownSeat],other=state.players[1-state.ownSeat];
  $("own-name").textContent=`${own.name} · 你${own.ready?" · 已准备":""}`;
  $("other-name").textContent=other?`${other.name}${!other.online?" · 掉线":other.ready?" · 已准备":""}`:"等待桌友入座";
  cards($("own-cards"),own.cards);cards($("other-cards"),other?.cards||[]);
  $("board-cards").hidden=state.game!=="holdem";cards($("board-cards"),state.board||[]);
  if(state.game==="blackjack"){$("own-name").textContent+=state.dealerSeat===state.ownSeat?" · 庄家":" · 闲家";$("other-name").textContent+=state.dealerSeat===state.ownSeat?" · 闲家":" · 庄家";}
  $("own-balance").textContent=`剩余 ${own.stack} · 本手投入 ${own.committed}`;
  $("other-balance").textContent=other?`剩余 ${other.stack} · 本手投入 ${other.committed}`:"把房间码发给好友";
  $("pot").textContent=state.pot;
  $("turn").textContent=state.phase==="paused"?"掉线暂停 · 等待重连":state.phase==="playing"?(state.actor===state.ownSeat?"轮到你":`等待 ${other?.name}`):state.phase==="finished"?"本手结束":"双方准备后发牌";
  clear($("actions"));$("amount-wrap").hidden=true;
  if(state.phase==="waiting"||state.phase==="finished")button(own.ready?"取消准备":state.phase==="finished"?"准备下一手":"准备发牌",()=>send("ready",{ready:!own.ready}),!own.ready);
  if(state.phase==="playing"){
    const wager=state.legalMoves.find(m=>m.move==="bet"||m.move==="raise");
    if(wager){
      $("amount-wrap").hidden=false;const amount=$("amount");amount.min=wager.amount;amount.max=wager.max;
      if(!amount.value||Number(amount.value)<wager.amount||Number(amount.value)>wager.max)amount.value=wager.amount;
    }
    for(const move of state.legalMoves){button(`${labels[move.move]}${move.amount?` ${move.amount}`:""}`,()=>{
      const data={move:move.move};if(move.move==="bet"||move.move==="raise")data.amount=Number($("amount").value);send("act",data);
    },move.move==="allIn");
      if(move.move==="bet"||move.move==="raise"){const node=$("actions").lastElementChild;node.dataset.wager=move.move;node.textContent=`${labels[move.move]} ${$("amount").value}`;}
    }
  }
  $("result").hidden=!state.result;
  if(state.result){const result=state.result;$("result").textContent=(result.winnerSeat<0?"平局，退回本手原押":`${result.winnerSeat===state.ownSeat?"你":other?.name||"对手"}收下底池 ${result.pot} · 你本手净${result.net[state.ownSeat]>=0?"赢":"输"} ${Math.abs(result.net[state.ownSeat])}`)+(result.description?` · ${result.description}`:"");}
  clear($("events"));
  for(const event of state.events){const li=document.createElement("li");const who=state.players[event.seat]?.name||"离桌玩家";li.textContent=event.kind==="table_reset"?`桌友入座，双方带入 ${event.amount} 测试筹码`:event.kind==="deal"?`第 ${event.hand} 手发牌`:event.kind==="action"?`第 ${event.round} 轮 · ${who} ${labels[event.move]}${event.amount?` ${event.amount}`:""}`:event.kind==="disconnect_timeout"?`${who} 重连超时`:event.kind==="idle_timeout"?"牌桌闲置关闭":`${who} 离桌`;$("events").append(li);}
  $("leave").disabled=pending;
}
setInterval(()=>{if(socket?.readyState===WebSocket.OPEN)socket.send(JSON.stringify({type:"ping"}));},15000);
if(token)connect();
function renderDirectory(){
  $("directory").hidden=!directory||!$("login").hidden||!features.includes("lobby");if(!directory)return;
  $("online-count").textContent=`在线 ${directory.totalOnline}`;
  $("player-filter").value=directory.filter||"all";$("allow-visits").checked=!!directory.privacy?.allowVisits;$("allow-invites").checked=directory.privacy?.allowInvites!==false;
  const row=(parent,text,actions=[])=>{const item=document.createElement("div");item.className="directory-row";const label=document.createElement("span");label.textContent=text;item.append(label);for(const [title,action,disabled]of actions){const btn=document.createElement("button");btn.textContent=title;btn.disabled=disabled||pending||socket?.readyState!==WebSocket.OPEN;btn.addEventListener("click",action);item.append(btn);}parent.append(item);};
  clear($("online-players"));clear($("public-rooms"));clear($("invitations"));
  for(const player of directory.players)row($("online-players"),`${player.name} · ${player.provider==="steam"?"Steam":player.provider==="password"?"账号":"测试"} · ${player.status==="lobby"?"大厅":player.status==="playing"?"对局中":"房间中"}`,player.id!==directory.selfId&&state&&state.players.some(s=>!s)?[["邀请",()=>send("invite",{target:player.id}),player.allowInvites===false]]:[]);
  for(const room of directory.rooms)row($("public-rooms"),`${room.name} · ${room.economy==="store"?"游戏信用点（游戏端入座）":"练习筹码"} · ${games[room.game]} · ${room.seats}/${room.capacity||2}`,[["入座",()=>send("join",{code:room.code}),room.economy==="store"||!!state||!room.canJoin&&room.seats>=(room.capacity||2)||room.playing]]);
  if(!directory.rooms.length)row($("public-rooms"),"暂无公开房间");
  for(const invite of directory.invitations)row($("invitations"),`${invite.fromName} 邀你玩 ${games[invite.game]}`,[["接受",()=>send("acceptInvite",{invitation:invite.id}),!!state],["谢绝",()=>send("declineInvite",{invitation:invite.id}),false]]);
  if(!directory.invitations.length)row($("invitations"),"暂无邀请");
}

let selectedCards=new Set(),selectedTile=null,groupKey="";
const tileName=t=>t>=27?["东","南","西","北","白","发","中"][t-27]:"一二三四五六七八九"[t%9]+["万","筒","条"][Math.floor(t/9)];
function renderGroup(){
  const own=state.players[state.ownSeat],detail=state.detail,playing=state.phase==="playing",key=state.code+":"+state.hand;
  if(groupKey!==key){selectedCards.clear();selectedTile=null;groupKey=key;}
  $("code").textContent=state.code;$("round").textContent=games[state.game]+" · 第 "+state.hand+" 局";$("mode-rules").textContent=rules[state.game];
  $("own-name").textContent=own.name+" · 你"+(own.ready?" · 已准备":"");$("own-balance").textContent="余筹码 "+own.stack;
  $("other-name").textContent="同桌玩家";clear($("other-cards"));$("other-balance").textContent="";$("board-cards").hidden=true;
  for(let seat=0;seat<state.players.length;seat++)if(seat!==state.ownSeat){
    const player=state.players[seat],line=document.createElement("p");
    line.textContent=player?player.name+(player.bot?" · 人机":"")+" · "+player.cards.length+" 张 · 余筹码 "+player.stack+(player.ready?" · 已准备":"")+(state.actor===seat?" · 正在行动":""):"空座位";
    $("other-cards").append(line);
  }
  clear($("own-cards"));
  if(state.game==="landlord"){
    for(const value of own.cards){const holder=document.createElement("span");cards(holder,[value]);const card=holder.firstElementChild;card.style.cursor="pointer";card.style.transform=selectedCards.has(value)?"translateY(-12px)":"";card.addEventListener("click",()=>{selectedCards.has(value)?selectedCards.delete(value):selectedCards.add(value);renderGroup();});$("own-cards").append(card);}
  }else if(state.game==="mahjong"){
    for(const value of own.cards){const card=document.createElement("button");card.className="card";card.textContent=tileName(value);card.title="点选手牌，再点打出选中牌";card.style.transform=selectedTile===value?"translateY(-8px)":"";card.addEventListener("click",()=>{selectedTile=value;renderGroup();});$("own-cards").append(card);}
  }
  $("pot").textContent=state.game==="dice"?state.pot:"底分 "+state.ante;
  $("turn").textContent=state.phase==="paused"?"有玩家掉线，等待重连":playing?(state.actor===state.ownSeat?"轮到你":"等待 "+state.players[state.actor]?.name):state.phase==="finished"?"本局结束":"坐满并各自准备后开局";
  clear($("events"));
  if(detail&&state.game==="landlord"){
    const line=document.createElement("li");line.textContent=detail.boss<0?"等待叫地主":"地主："+state.players[detail.boss].name;$("events").append(line);
    if(detail.last.length){const row=document.createElement("li");row.textContent="上一手：";cards(row,detail.last);$("events").append(row);}
  }else if(detail&&state.game==="mahjong"){
    for(let seat=0;seat<state.players.length;seat++){
      const row=document.createElement("li");row.textContent=state.players[seat].name+" · 弃牌："+detail.rivers[seat].map(tileName).join("、")+"；副露："+detail.melds[seat].map(t=>t<0?"暗牌":tileName(t)).join("、");$("events").append(row);
    }
    if(detail.waits.length){const row=document.createElement("li");row.textContent="听 "+detail.waits.map(tileName).join("、");$("events").append(row);}
  }else if(detail?.faces?.length)$("other-balance").textContent="骰子："+detail.faces.join("、");
  clear($("actions"));$("amount-wrap").hidden=true;
  if(["waiting","finished"].includes(state.phase)){
    button(own.ready?"取消准备":"准备开局",()=>send("ready",{ready:!own.ready}),true);
    if(state.capacity>2&&state.hostId===own.id){
      if(state.players.some(p=>!p))button("添加人机",()=>send("botAdd"));
      if(state.players.some(p=>p?.bot))button("移除人机",()=>send("botRemove"));
    }
  }
  if(playing)for(const option of state.legalMoves){
    const label=(labels[option.move]||option.move)+(option.choice!==null&&option.choice!==undefined?" "+tileName(option.choice):"");
    button(label,()=>{const data={move:option.move};if(option.choice!==null&&option.choice!==undefined)data.choice=option.choice;
      if(option.move==="play"){data.cards=Array.from(selectedCards);selectedCards.clear();}
      if(option.move==="discard"){if(selectedTile===null){message("先选一张手牌。");return;}data.choice=selectedTile;selectedTile=null;}send("act",data);
    },["win","selfWin","play"].includes(option.move));
  }
  $("result").hidden=!state.result;if(state.result)$("result").textContent="本局净"+(state.result.net[state.ownSeat]>=0?"赢 ":"输 ")+Math.abs(state.result.net[state.ownSeat])+" 筹码 · "+state.result.description;
  $("leave").disabled=pending;
}
