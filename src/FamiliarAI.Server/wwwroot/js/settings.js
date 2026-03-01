/**
 * アプリケーション設定モジュール
 * サーバーサイドから渡される設定値を管理
 */
export const createSettings = (config) => ({
    ...config,

    /**
     * アバター画像のパスを生成
     * @param {boolean} eyesOpen - 目が開いているか
     * @param {boolean} mouthOpen - 口が開いているか
     * @returns {string} 画像パス
     */
    getCharacterImagePath: (eyesOpen = true, mouthOpen = false) => {
        const eyes = eyesOpen ? 'open' : 'close';
        const mouth = mouthOpen ? 'open' : 'close';
        return `/character-images/${eyes}_${mouth}.png`;
    },

    /**
     * デフォルト（アイドル）状態の画像パス
     * @returns {string} 画像パス
     */
    getIdleImagePath: () => {
        return `/character-images/open_close.png`;
    }
});
