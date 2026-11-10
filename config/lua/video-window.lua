-- Zephyron: малое окно приёма в начале соединения (узлы видео YouTube под фильтром).
--
-- Фильтр у части групп серверов Google режет ответ сервера по первому пакету с данными:
-- запись TLS узнаётся по заголовку в три байта. Если окно приёма у клиента в один байт,
-- сервер дробит ответ, первый пакет несёт один байт, и фильтр его пропускает. Дальше окно
-- открывается само. Проверено 10.11.2026: узел 74.125.154.103 без приёма молчал 6 с,
-- с ним и обычным десинком googlevideo ответил 204 за 1,1 с.
--
-- Окно в один байт осмысленно только без масштаба окна, поэтому в SYN опция масштаба
-- убирается: соединение целиком живёт без него (до 64 КБ в пути). Затем окно 1 ставится
-- на подтверждение рукопожатия и на ClientHello, пока от сервера не придёт open байт.
-- Подтверждения без данных winws2 видит, только если их пустили в перехват (--wf-tcp-empty).
--
-- arg: wsize=N — окно, пока не пришло open байт (по умолчанию 1)
-- arg: open=N  — после скольких байт ответа окно больше не трогать (по умолчанию 2)
function zy_video_window(ctx, desync)
	local dis = desync.dis
	if not dis.tcp or not desync.outgoing then return end

	local wsize = tonumber(desync.arg.wsize or 1)
	local open = tonumber(desync.arg.open or 2)

	if bitand(dis.tcp.th_flags, bitor(TH_SYN, TH_ACK)) == TH_SYN then
		local i = find_tcp_option(dis.tcp.options, TCP_KIND_SCALE)
		if i then table.remove(dis.tcp.options, i) end
		DLOG("zy_video_window: SYN without window scale")
		return VERDICT_MODIFY
	end

	if not desync.track then return end

	local state = desync.track.lua_state
	if not state.zy_window_ack0 then state.zy_window_ack0 = dis.tcp.th_ack end

	local got = u32add(dis.tcp.th_ack, -state.zy_window_ack0)
	if got < open then
		dis.tcp.th_win = wsize
		return VERDICT_MODIFY
	end

	DLOG("zy_video_window: "..got.." bytes received, window left to the stack")
	instance_cutoff_shim(ctx, desync)
end
