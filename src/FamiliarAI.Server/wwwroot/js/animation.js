/**
 * アニメーション管理モジュール
 * タイプライター効果、日本語音節に基づく口パクアニメーション、瞬きを管理
 */
import { sleep } from './utils.js';

/**
 * 日本語テキストの音節解析クラス
 */
class JapanesePhonemeAnalyzer {
    constructor() {
        // 母音パターン（口を開くタイミング）
        this.vowels = /[あいうえおアイウエオぁぃぅぇぉァィゥェォ]/;
        // 長音（母音と同様に扱う）
        this.longVowel = /[ー～]/;
        // 促音（短く口を開く）
        this.sokuon = /っッ/;
        // 撥音（鼻音、口は閉じたまま）
        this.hatsuon = /んン/;
        // 句読点や空白（停止）
        this.pause = /[、。！？\.\,\?\!\s]/;
        // 英文字の母音
        this.latinVowels = /[aeiouAEIOU]/;
    }

    /**
     * テキストを解析して音節情報の配列を返す
     * @param {string} text - 解析するテキスト
     * @returns {Array<{char: string, type: string, duration: number}>} 音節情報
     */
    analyze(text) {
        const phonemes = [];

        for (let i = 0; i < text.length; i++) {
            const char = text[i];
            const info = this.getPhonemeInfo(char);
            phonemes.push(info);
        }

        return phonemes;
    }

    /**
     * 文字の音節情報を取得
     * @param {string} char - 1文字
     * @returns {{char: string, type: string, duration: number}} 音節情報
     */
    getPhonemeInfo(char) {
        if (this.vowels.test(char)) {
            return { char, type: 'vowel', duration: 1.0 };
        }
        if (this.longVowel.test(char)) {
            return { char, type: 'long_vowel', duration: 0.8 };
        }
        if (this.sokuon.test(char)) {
            return { char, type: 'sokuon', duration: 0.3 };
        }
        if (this.hatsuon.test(char)) {
            return { char, type: 'hatsuon', duration: 0.5 }; // んは口を閉じる
        }
        if (this.pause.test(char)) {
            return { char, type: 'pause', duration: 0.2 };
        }
        if (this.latinVowels.test(char)) {
            return { char, type: 'vowel', duration: 0.8 };
        }
        // 子音やその他の文字
        return { char, type: 'consonant', duration: 0.4 };
    }

    /**
     * 口を開けるべきか判定
     * @param {string} type - 音節タイプ
     * @returns {boolean} 口を開けるかどうか
     */
    shouldOpenMouth(type) {
        return type === 'vowel' || type === 'long_vowel' || type === 'sokuon';
    }
}

export class AnimationManager {
    constructor(settings) {
        this.settings = settings;
        this.avatarImg = document.getElementById('avatar-img');
        this.output = document.getElementById('output');

        // 音節解析器
        this.phonemeAnalyzer = new JapanesePhonemeAnalyzer();

        // アニメーション制御
        this.isTalking = false;
        this.currentMouthOpen = false;
        this.currentEyesOpen = true;

        // ストリーミング口パクキュー
        this.chunkQueue = [];
        this.isAnimatingChunk = false;
        this.chunkCharDelay = 80; // ms/char（発話速度基準）

        // 瞬き制御
        this.blinkInterval = null;
        this.isBlinking = false;
        this.nextBlinkTime = 3000;

        // 瞬き開始
        this.startBlinking();
    }

    /**
     * キャラクター画像を更新
     * @param {boolean} eyesOpen - 目が開いているか
     * @param {boolean} mouthOpen - 口が開いているか
     */
    updateCharacterImage(eyesOpen = true, mouthOpen = false) {
        this.currentEyesOpen = eyesOpen;
        this.currentMouthOpen = mouthOpen;
        this.avatarImg.src = this.settings.getCharacterImagePath(eyesOpen, mouthOpen);
    }

    // ==================== 瞬きアニメーション ====================

    /**
     * 瞬きアニメーションを開始
     */
    startBlinking() {
        if (this.blinkInterval) {
            clearTimeout(this.blinkInterval);
        }

        const scheduleNextBlink = () => {
            // ランダムな間隔（2秒〜6秒）
            this.nextBlinkTime = 2000 + Math.random() * 4000;

            this.blinkInterval = setTimeout(() => {
                this.performBlink();
                scheduleNextBlink();
            }, this.nextBlinkTime);
        };

        scheduleNextBlink();
    }

    /**
     * 瞬きを実行
     */
    async performBlink() {
        if (this.isBlinking) return;

        this.isBlinking = true;

        // 目を閉じる（口の状態は維持）
        this.updateCharacterImage(false, this.currentMouthOpen);

        // 150ms後に目を開く
        await sleep(150);

        this.isBlinking = false;
        this.updateCharacterImage(true, this.currentMouthOpen);
    }

    /**
     * 瞬きを停止
     */
    stopBlinking() {
        if (this.blinkInterval) {
            clearTimeout(this.blinkInterval);
            this.blinkInterval = null;
        }
    }

    // ==================== WebSocket用リアルタイム制御 ====================

    /**
     * 話し始め（ストリーミング開始時）
     */
    startTalking() {
        this.isTalking = true;
        this.updateCharacterImage(true, true); // 目開き、口開き
    }

    /**
     * 話し終わり（ストリーミング終了時）
     */
    stopTalking() {
        this.isTalking = false;
        this.chunkQueue = [];
        this.isAnimatingChunk = false;
        this.updateCharacterImage(true, false); // 目開き、口閉じ
    }

    /**
     * ストリーミングチャンクの音節アニメーション
     * @param {string} text - 受信チャンクテキスト
     */
    processChunk(text) {
        if (!this.isTalking) return;
        this.chunkQueue.push(text);
        if (!this.isAnimatingChunk) {
            this._drainChunkQueue();
        }
    }

    /**
     * チャンクキューを順番に処理
     */
    async _drainChunkQueue() {
        this.isAnimatingChunk = true;
        while (this.chunkQueue.length > 0 && this.isTalking) {
            // バックログが多いほど速くする
            const backlog = this.chunkQueue.length;
            const speed = backlog > 3 ? 0.3 : backlog > 1 ? 0.6 : 1.0;
            const text = this.chunkQueue.shift();
            const phonemes = this.phonemeAnalyzer.analyze(text);
            for (const phoneme of phonemes) {
                if (!this.isTalking) break;
                this.updateCharacterImage(this.currentEyesOpen,
                    this.phonemeAnalyzer.shouldOpenMouth(phoneme.type));
                await sleep(this.chunkCharDelay * phoneme.duration * speed);
            }
        }
        this.isAnimatingChunk = false;
    }

    /**
     * アニメーションを破棄（クリーンアップ）
     */
    dispose() {
        this.stopBlinking();
        this.isTalking = false;
    }
}
