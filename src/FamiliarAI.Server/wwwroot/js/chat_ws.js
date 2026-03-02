/**
 * チャット機能モジュール (WebSocket版)
 * メッセージの送受信とUI更新を管理
 */
import { sleep } from './utils.js';

export class ChatManager {
    constructor(settings, animationManager) {
        this.settings = settings;
        this.animationManager = animationManager;
        this.output = document.getElementById('output');
        this.input = document.getElementById('input');
        this.btnSend = document.getElementById('btn-send');
        this.btnClear = document.getElementById('btn-clear');
        this.statusPanel = document.getElementById('status-panel');
        this.statusText = document.getElementById('status-text');
        this.ws = null;
        this.reconnectInterval = 5000;
        this.currentAiWrap = null;
        this.currentAiText = null;
        this.isProcessing = false;

        // タイプライター用文字キュー
        this.displayQueue = [];
        this.isDraining = false;
        this.charDelay = settings.typewriterDelay ?? 22; // ms/char

        this.initWebSocket();
        this.initEventListeners();
    }

    // WebSocket接続初期化
    initWebSocket() {
        const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        const wsUrl = `${protocol}//${window.location.host}/ws`;

        console.log(`Connecting to WebSocket: ${wsUrl}`);
        this.ws = new WebSocket(wsUrl);

        this.ws.onopen = () => {
            console.log('WebSocket connected');
        };

        this.ws.onclose = () => {
            console.log('WebSocket disconnected');
            this._setStatus('Disconnected', 'disconnected');
            this.addSystem('Disconnected from server');
            setTimeout(() => this.initWebSocket(), this.reconnectInterval);
        };

        this.ws.onerror = (error) => {
            console.error('WebSocket error:', error);
            this.addSystem('Connection error');
        };

        this.ws.onmessage = (event) => {
            this.handleMessage(JSON.parse(event.data));
        };
    }

    // メッセージハンドリング
    handleMessage(data) {
        switch (data.type) {
            case 'connected': {
                const agentName = data.data?.agent_name || this.settings.agentName || 'Agent';
                this._setStatus(`Connected: ${agentName}`, 'connected');
                this.addSystem(`Connected to ${agentName}`);
                break;
            }

            case 'user_message':
                this.addUser(data.data.message);
                break;

            case 'text_chunk':
                if (!this.currentAiWrap) {
                    this.startAiLine();
                }
                this.appendToAiLine(data.data.chunk);
                this.animationManager.processChunk(data.data.chunk);
                break;

            case 'action':
                this.addAction(data.data);
                break;

            case 'response_complete':
                this._endResponse();
                break;

            case 'error':
                this._endResponse(data.data.message);
                break;

            case 'history_cleared':
                this.output.innerHTML = '';
                this.addSystem('History cleared');
                break;

            case 'status': {
                const msg = data.data?.message || '';
                this._setStatus(msg, null);
                console.log('Status:', msg);
                break;
            }

            default:
                console.log('Unknown message type:', data.type);
        }
    }

    // イベントリスナー初期化
    initEventListeners() {
        this.input.addEventListener('keydown', async (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                const message = this.input.value.trim();
                if (!message) return;
                if (message === '/clear') {
                    this.input.value = '';
                    this.sendCommand('clear_history');
                    return;
                }
                if (!this.isProcessing) {
                    this.input.value = '';
                    await this.sendMessage(message);
                }
            }
        });

        this.btnSend.addEventListener('click', async () => {
            const message = this.input.value.trim();
            if (!message || this.isProcessing) return;
            this.input.value = '';
            await this.sendMessage(message);
        });

        this.btnClear.addEventListener('click', () => {
            this.sendCommand('clear_history');
        });
    }

    // メッセージ送信
    async sendMessage(message) {
        if (this.isProcessing || this.ws.readyState !== WebSocket.OPEN) return;

        this.isProcessing = true;
        this._setInputEnabled(false);
        this.animationManager.startTalking();

        this.ws.send(JSON.stringify({
            type: 'chat',
            data: { message }
        }));
    }

    // コマンド送信
    sendCommand(command) {
        if (this.ws.readyState !== WebSocket.OPEN) return;
        this.ws.send(JSON.stringify({ type: command, data: null }));
    }

    // ===== UI ヘルパー =====

    _endResponse(errorMessage = null) {
        this._flushDisplayQueue();
        if (errorMessage != null) this.addError(errorMessage);
        this.isProcessing = false;
        this.currentAiWrap = null;
        this.currentAiText = null;
        this._setInputEnabled(true);
        this.animationManager.stopTalking();
    }

    _setInputEnabled(enabled) {
        this.input.disabled = !enabled;
        this.btnSend.disabled = !enabled;
        if (enabled) this.input.focus();
    }

    // ステータスパネルを更新
    // state: 'connected' | 'disconnected' | null (neutral)
    _setStatus(text, state) {
        if (this.statusText) this.statusText.textContent = text;
        if (this.statusPanel) {
            this.statusPanel.classList.remove('connected', 'disconnected');
            if (state) this.statusPanel.classList.add(state);
        }
    }

    addSystem(text) {
        const el = document.createElement('div');
        el.className = 'msg-system';
        el.textContent = text;
        this.output.appendChild(el);
        this.scrollToBottom();
    }

    addUser(text) {
        const wrap = document.createElement('div');
        wrap.className = 'msg-user-wrap';

        const label = document.createElement('div');
        label.className = 'msg-user-label';
        label.textContent = this.settings.companionName || 'User';

        const bubble = document.createElement('div');
        bubble.className = 'msg-user';
        bubble.textContent = text;

        wrap.appendChild(label);
        wrap.appendChild(bubble);
        this.output.appendChild(wrap);
        this.scrollToBottom();
    }

    // AIアクションを開始（タイプライター対応）
    startAiLine() {
        const wrap = document.createElement('div');
        wrap.className = 'msg-action-wrap';

        const label = document.createElement('div');
        label.className = 'msg-action-label';
        label.textContent = 'Action';

        const bubble = document.createElement('div');
        bubble.className = 'msg-action';

        const icon = document.createElement('span');
        icon.className = 'action-icon';
        icon.textContent = '💬';

        const textSpan = document.createElement('span');
        textSpan.className = 'ai-text';

        bubble.appendChild(icon);
        bubble.appendChild(textSpan);
        wrap.appendChild(label);
        wrap.appendChild(bubble);
        this.output.appendChild(wrap);

        this.currentAiWrap = wrap;
        this.currentAiText = textSpan;
        this.scrollToBottom();
    }

    appendToAiLine(text) {
        if (!this.currentAiText) return;
        for (const char of text) {
            this.displayQueue.push(char);
        }
        if (!this.isDraining) {
            this._drainDisplayQueue();
        }
    }

    async _drainDisplayQueue() {
        this.isDraining = true;
        while (this.displayQueue.length > 0) {
            const backlog = this.displayQueue.length;
            const delay = backlog > 30 ? 0 : backlog > 15 ? 8 : this.charDelay;
            const char = this.displayQueue.shift();
            if (this.currentAiText) {
                this.currentAiText.textContent += char;
                this.scrollToBottom();
            }
            if (delay > 0) await sleep(delay);
        }
        this.isDraining = false;
    }

    _flushDisplayQueue() {
        while (this.displayQueue.length > 0) {
            const char = this.displayQueue.shift();
            if (this.currentAiText) {
                this.currentAiText.textContent += char;
            }
        }
        this.isDraining = false;
        this.scrollToBottom();
    }

    // ツール実行アクションを表示（text_chunk以外のアクション通知）
    addAction(data) {
        const wrap = document.createElement('div');
        wrap.className = 'msg-action-wrap';

        const label = document.createElement('div');
        label.className = 'msg-action-label';
        label.textContent = 'Action';

        const bubble = document.createElement('div');
        bubble.className = 'msg-action';
        bubble.innerHTML = `<span class="action-icon">${data.icon || '⚙'}</span>${data.label || data.name || ''}`;

        wrap.appendChild(label);
        wrap.appendChild(bubble);
        this.output.appendChild(wrap);
        this.scrollToBottom();
    }

    addError(message) {
        const el = document.createElement('div');
        el.className = 'msg-error';
        el.textContent = `ERROR: ${message}`;
        this.output.appendChild(el);
        this.scrollToBottom();
    }

    scrollToBottom() {
        this.output.scrollTop = this.output.scrollHeight;
    }

}
