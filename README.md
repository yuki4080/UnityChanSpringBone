# UnityChanSpringBone Burst
UnityChan Sping Bone System for lightweight secondary animations.

And this repository was customized using JobSystem/Burst Compiler.



## How to set up (example)

- [SunnySideUp UnityChan](https://unity-chan.com/contents/news/3878/) のプロジェクトデータをダウンロードしてください
- **Packages/UnityChanSpringBone-release-1.1** を削除します
- 本リポジトリをダウンロードして、**Packages**ディレクトリ以下に展開してください



## How to use

- SpringManagerを持つGameObject（rootノード可）を選択
- **UTJ/選択したSpringBoneをJob化** を選択、実行



## Attention!

- Job化したSpringManagerを元に戻すことは出来ません
- Job化したSpringBone、SpringColliderを再編集することは出来ません
- 上記２つはどちらも未実装であるだけで作れば可能です、必要があれば拡張してください



## Required

Unity 2019.4 LTS
Burst Compiler v1.3.4 (verified)



## License

UnityChanSpringBone
Copyright (c) 2018 Unity Technologies
Code released under the MIT License
https://opensource.org/licenses/mit-license.php

//----------------------------------------------------------------------------

TaskSystem.csNativeContainerPool.cs
Copyright (c) 2020 Yugo Fujioka
Code released under the MIT License
https://opensource.org/licenses/mit-license.php

//----------------------------------------------------------------------------

TaskSystem.cs
Copyright (c) 2017 Yugo Fujioka
Code released under the MIT License
https://opensource.org/licenses/mit-license.php