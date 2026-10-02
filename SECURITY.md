# セキュリティと患者データ

- VolumeScope はネットワークに接続しません。読み込んだ画像は、この PC のメモリの中だけで扱います。
- 書き出すのは、利用者が選んだ PNG（画面）と STL / OBJ（面）だけです。STL / OBJ には患者の名前や ID を入れません（STL の見出しは組織名と閾値のみ）。
- 画面の画像（PNG）には、上部の患者名・ID が写ることがあります。外部に渡すときは注意してください。
- エラーの記録は `%LOCALAPPDATA%\VolumeScope\logs` に保存します（画像のデータは含みません）。
- 脆弱性を見つけたときは、公開の Issue ではなく GitHub の「Security → Report a vulnerability」から知らせてください。
