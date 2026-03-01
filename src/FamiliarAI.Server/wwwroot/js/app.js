/**
 * メインアプリケーションモジュール (WebSocket版)
 */
import { ChatManager } from './chat_ws.js';
import { AnimationManager } from './animation.js';
import { createSettings } from './settings.js';

// DOM読み込み完了後に初期化
document.addEventListener('DOMContentLoaded', () => {
    // 設定の初期化
    const settings = createSettings(appConfig);

    // エージェント名をDOMに反映
    document.title = appConfig.agentName;
    const avatarImg = document.getElementById('avatar-img');
    if (avatarImg) avatarImg.alt = appConfig.agentName;

    // アニメーションマネージャーの初期化
    const animationManager = new AnimationManager(settings);

    // チャットマネージャーの初期化（WebSocket版）
    const chatManager = new ChatManager(settings, animationManager);

    console.log('Familiar AI Client initialized (WebSocket)');
});
