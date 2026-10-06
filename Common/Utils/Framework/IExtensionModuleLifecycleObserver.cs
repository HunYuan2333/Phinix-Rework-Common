namespace Utils.Framework
{
    /// <summary>Generic host resources follow module activation, including failed activation.</summary>
    public interface IExtensionModuleLifecycleObserver
    {
        void OnActivating(IPhinixExtensionModule module);
        void OnStopped(IPhinixExtensionModule module);
    }
}
