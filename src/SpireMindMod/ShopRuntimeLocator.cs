namespace SpireMindMod;

internal static class ShopRuntimeLocator
{
    public static object? FindShopScreen(ShopRuntimeLocatorContext context)
    {
        object? screenContext = context.GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext",
            "Instance");
        object? currentScreen = context.TryInvokeMethod(screenContext, "GetCurrentScreen");
        if (IsShopScreen(context, currentScreen))
        {
            return currentScreen;
        }

        object? overlayStack = context.GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack",
            "Instance");
        object? topOverlay = context.TryInvokeMethod(overlayStack, "Peek");
        if (IsShopScreen(context, topOverlay))
        {
            return topOverlay;
        }

        object? runManager = context.GetStaticPropertyValue("MegaCrit.Sts2.Core.Runs.RunManager", "Instance");
        object? currentRoom = context.FindMemberValue(runManager, "CurrentRoom", "currentRoom", "_currentRoom");
        object? nGame = context.GetStaticPropertyValue("MegaCrit.Sts2.Core.Nodes.NGame", "Instance");

        return context.EnumerateNodeDescendants(currentScreen)
            .Concat(context.EnumerateNodeDescendants(overlayStack))
            .Concat(context.EnumerateNodeDescendants(currentRoom))
            .Concat(context.EnumerateNodeDescendants(nGame))
            .FirstOrDefault(candidate => IsShopScreen(context, candidate));
    }

    public static object? FindCurrentRoom(ShopRuntimeLocatorContext context, object? runState, object? runManager)
    {
        return context.FindMemberValue(runState, "CurrentRoom", "currentRoom", "_currentRoom")
            ?? context.FindMemberValue(runManager, "CurrentRoom", "currentRoom", "_currentRoom");
    }

    public static object? FindRuntimeMerchantInventory(
        ShopRuntimeLocatorContext context,
        object? player,
        object? runState,
        object? currentRoom,
        IEnumerable<object?>? graphValues)
    {
        object? playerRunState = context.FindMemberValue(player, "RunState", "runState", "_runState");
        object? merchantRoom = IsMerchantRoom(currentRoom)
            ? currentRoom
            : FindCurrentRoom(context, runState ?? playerRunState, null);
        object? inventory = context.FindMemberValue(merchantRoom, "Inventory", "inventory", "_inventory");
        if (inventory is not null)
        {
            return inventory;
        }

        if (graphValues is null)
        {
            return null;
        }

        return graphValues
            .Where(value => value is not null)
            .FirstOrDefault(value =>
            {
                string typeName = value!.GetType().FullName ?? value.GetType().Name;
                return typeName.Contains("MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory", StringComparison.Ordinal);
            });
    }

    public static object? FindShopInventory(ShopRuntimeLocatorContext context, object shopScreen, IEnumerable<object?> graphValues)
    {
        object? directInventory = context.FindMemberValue(
            shopScreen,
            "Inventory",
            "inventory",
            "_inventory",
            "MerchantInventory",
            "merchantInventory",
            "_merchantInventory");
        if (directInventory is not null)
        {
            return directInventory;
        }

        return graphValues
            .Where(value => value is not null)
            .FirstOrDefault(value =>
            {
                string typeName = value!.GetType().FullName ?? value.GetType().Name;
                return ContainsAny(typeName, "MerchantInventory", "ShopInventory", "StoreInventory");
            });
    }

    public static bool IsMerchantRoom(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return typeName.Equals("MegaCrit.Sts2.Core.Rooms.MerchantRoom", StringComparison.Ordinal)
            || typeName.EndsWith(".MerchantRoom", StringComparison.Ordinal);
    }

    private static bool IsShopScreen(ShopRuntimeLocatorContext context, object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return ContainsAny(typeName, "Shop", "Merchant", "Store")
            && context.IsLiveVisibleControl(source);
    }

    private static bool ContainsAny(string? text, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record ShopRuntimeLocatorContext(
    ShopStaticPropertyReader GetStaticPropertyValue,
    ShopMethodInvoker TryInvokeMethod,
    ShopMemberValueReader FindMemberValue,
    Func<object?, IEnumerable<object>> EnumerateNodeDescendants,
    Func<object, bool> IsLiveVisibleControl);

internal delegate object? ShopStaticPropertyReader(string typeName, string propertyName);

internal delegate object? ShopMethodInvoker(object? source, string methodName, params object?[] args);
