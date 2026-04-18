# InteractiveCameraTweak — 開発知見まとめ

## プロジェクト概要

ResoniteのInteractiveCameraがInteractiveCameraControlと連携したときに処理が重くなる問題を修正するBepInEx MOD。

- **フレームワーク**: BepInEx + HarmonyX (HarmonyLib)
- **ターゲット**: .NET 10 / `net10.0`
- **デコンパイル済みソース**: `C:\Users\star_\AppData\Roaming\com.kesomannen.gale\resonite\profiles\AudioClient\reso-decompile\sources\`

## ビルドとデプロイ

```
dotnet build
```

ビルド成功後、自動的に以下へコピーされる:

```
%APPDATA%\com.kesomannen.gale\resonite\profiles\InteractiveCameraTweak\BepInEx\plugins\InteractiveCameraTweak\
```

Resoniteが起動中の場合はDLLがロックされてコピーに失敗する。先にResoniteを閉じること。

Thunderstoreへの公開は `thunderstore.toml` + `tcli` で行う（Releaseビルドが対象）。

## 問題の根本原因

`InteractiveCamera.OnCommonUpdate()` 内の `if (Control != null)` ブロックで、毎フレーム次の2処理が実行されていた。

### 1. アンカースキャン（毎フレーム）

```csharp
// FrooxEngine/InteractiveCamera.cs ~line 579
List<InteractiveCameraAnchor> list = Pool.BorrowList<InteractiveCameraAnchor>();
base.World.GetGloballyRegisteredComponents(list); // ワールド全体をスキャン — 高コスト
foreach (InteractiveCameraAnchor item in list) { ... }
```

`World.GetGloballyRegisteredComponents<InteractiveCameraAnchor>` がワールドのグローバルレジストリを毎フレーム全走査する。

### 2. バッジ非表示スキャン（HideBadge=true のとき毎フレーム）

```csharp
// FrooxEngine/InteractiveCamera.cs ~line 606
AvatarManager.CollectAllBadgeRoots(list2, delegate(Slot root) { ... });
```

`CollectAllBadgeRoots` は内部で全ユーザーのアバター階層に対して `ForeachComponentInChildren<AvatarNameplateVisibilityDriver>` を実行する（O(ユーザー数 × アバター階層深さ)）。
結果は `_excludedBadges`（HashSet）にキャッシュされているにもかかわらず、毎フレーム再スキャンしていた。

## 修正方針

Harmony Transpiler で2つのメソッド呼び出しをスロットル版に差し替える。

### アンカースキャンの修正

`GetGloballyRegisteredComponents` を `ThrottledGetAnchors` に置き換え、0.25秒間隔（最大4回/秒）に制限。スキャンをスキップする場合はリストを空のまま返す（既存の除外設定はそのまま維持される）。

### バッジスキャンの修正

`CollectAllBadgeRoots` を `ThrottledCollectBadgeRoots` に置き換え、次の条件を満たすときのみ実行：
- `_excludedBadges` が空（= まだ適用されていない、またはHideBadgeがオフになってClearされた）
- またはワールドのユーザー数が変化した

`_excludedBadges` の空チェックには `AccessTools.FieldRefAccess` を使ってネイティブ相当の速度でアクセスする（`FieldInfo.GetValue` は毎フレーム呼ぶと重いため使わない）。

## Harmonyパターンのポイント

### Transpilerでのメソッド差し替え

インスタンスメソッド `callvirt` を静的メソッド `call` に置き換える場合、スタック上の引数は同じでよい。インスタンスメソッドの `this` は静的メソッドの第1引数として受け取れる。

```csharp
// 元: callvirt World::GetGloballyRegisteredComponents<T>(List<T>, Predicate<T>)
// スタック: world(this), list, filter
// 置換後: call ThrottledGetAnchors(World, List<T>, Predicate<T>)
codes[i] = new CodeInstruction(OpCodes.Call, throttledMethod);
```

### インスタンスコンテキストの渡し方

Transpilerで差し替えた静的メソッドにインスタンス参照を渡したい場合、`[ThreadStatic]` 変数をPrefix/Postfixで設定するパターンが有効。

```csharp
[ThreadStatic] static InteractiveCamera? _currentCamera;

static void Prefix(InteractiveCamera __instance) => _currentCamera = __instance;
static void Postfix() => _currentCamera = null;
```

FrooxEngineのワールド更新はシングルスレッドなので `[ThreadStatic]` で安全に渡せる。

### プライベートフィールドへの高速アクセス

`FieldInfo.GetValue()` は毎フレーム呼ぶには重すぎる。`AccessTools.FieldRefAccess` を使うこと。

```csharp
// NG: 毎フレームのリフレクション呼び出し
var fi = AccessTools.Field(typeof(Foo), "_bar");
var val = fi.GetValue(instance);

// OK: 初期化時に一度だけコンパイル、以降はネイティブ相当の速度
static readonly AccessTools.FieldRef<Foo, Bar> _barRef =
    AccessTools.FieldRefAccess<Foo, Bar>("_bar");
var val = _barRef(instance); // 高速
```

### ジェネリックメソッドの検索

`AccessTools.Method` ではジェネリックメソッドを直接取得できないため、手動で検索する。

```csharp
foreach (var m in typeof(World).GetMethods(...))
{
    if (m.Name == "GetGloballyRegisteredComponents"
        && m.IsGenericMethodDefinition
        && m.GetParameters().Length == 2)
    {
        return m.MakeGenericMethod(typeof(InteractiveCameraAnchor));
    }
}
```

### インスタンスごとの状態管理

`ConditionalWeakTable<TInstance, TState>` を使うとインスタンスのライフサイクルに追従して状態を管理できる（GCで自動的に解放される）。

```csharp
static readonly ConditionalWeakTable<InteractiveCamera, PatchState> _states = new();
static PatchState GetState(InteractiveCamera cam) => _states.GetOrCreateValue(cam);
```

## 関連ファイル

| ファイル | 役割 |
|---------|------|
| [InteractiveCameraTweak/InteractiveCameraPatch.cs](InteractiveCameraTweak/InteractiveCameraPatch.cs) | パッチ本体 |
| [InteractiveCameraTweak/Plugin.cs](InteractiveCameraTweak/Plugin.cs) | BepInExエントリポイント、`harmony.PatchAll()` を呼ぶ |
| [InteractiveCameraTweak/InteractiveCameraTweak.csproj](InteractiveCameraTweak/InteractiveCameraTweak.csproj) | ビルド設定、デプロイ先パス |
| [thunderstore.toml](thunderstore.toml) | Thunderstore公開設定 |
