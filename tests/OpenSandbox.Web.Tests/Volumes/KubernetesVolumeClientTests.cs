using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using OpenSandbox.Web.Infrastructure.Volumes;
using Xunit;

namespace OpenSandbox.Web.Tests.Volumes;

public sealed class KubernetesVolumeClientTests
{
    private static readonly VolumeManagerOptions Options = new()
    {
        ManagedNamespaceSelector = "opensandbox.io/volume-managed=true",
    };

    [Fact]
    public async Task ListManagedNamespaces_UsesSelectorAndMapsLabels()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK,
            """{"apiVersion":"v1","kind":"NamespaceList","items":[{"metadata":{"name":"opensandbox","labels":{"opensandbox.io/volume-managed":"true","team":"a"}}},{"metadata":{"name":"second","labels":{"opensandbox.io/volume-managed":"true"}}}]}"""));

        var namespaces = await client.ListManagedNamespacesAsync(CancellationToken.None);

        Assert.Equal(["opensandbox", "second"], namespaces.Select(x => x.Name));
        Assert.Equal("a", namespaces[0].Labels["team"]);
        Assert.Equal("true", namespaces[1].Labels[VolumeLabels.Managed]);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/v1/namespaces", request.Uri.AbsolutePath);
        Assert.Contains("labelSelector=opensandbox.io/volume-managed=true",
            Uri.UnescapeDataString(request.Uri.Query));
    }

    [Fact]
    public async Task GetNamespace_MapsLabels()
    {
        var (client, _) = CreateClient(_ => Json(HttpStatusCode.OK,
            """{"apiVersion":"v1","kind":"Namespace","metadata":{"name":"opensandbox","labels":{"opensandbox.io/volume-managed":"true"}}}"""));

        var info = await client.GetNamespaceAsync("opensandbox", CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("opensandbox", info!.Name);
        Assert.Equal("true", info.Labels[VolumeLabels.Managed]);
    }

    [Fact]
    public async Task GetNamespace_NotFound_ReturnsNull()
    {
        var (client, _) = CreateClient(_ => Json(HttpStatusCode.NotFound,
            """{"apiVersion":"v1","kind":"Status","status":"Failure","message":"not found","reason":"NotFound","code":404}"""));

        Assert.Null(await client.GetNamespaceAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task GetNamespace_ServerError_Throws()
    {
        var (client, _) = CreateClient(_ => Json(HttpStatusCode.InternalServerError,
            """{"apiVersion":"v1","kind":"Status","status":"Failure","message":"boom","code":500}"""));

        await Assert.ThrowsAsync<HttpOperationException>(() =>
            client.GetNamespaceAsync("opensandbox", CancellationToken.None));
    }

    [Fact]
    public async Task ListPvcs_MapsFieldsIncludingDeletionAndUnlabeledPvc()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK,
            """{"apiVersion":"v1","kind":"PersistentVolumeClaimList","items":[{"metadata":{"namespace":"opensandbox","name":"pvc-1","uid":"uid-1","creationTimestamp":"2026-10-01T00:00:00Z","deletionTimestamp":"2026-10-20T00:00:00Z","labels":{"opensandbox.io/volume-lifecycle":"business"}},"spec":{"storageClassName":"standard","resources":{"requests":{"storage":"1Gi"}}},"status":{"phase":"Bound","capacity":{"storage":"1Gi"}}},{"metadata":{"namespace":"opensandbox","name":"pvc-2","uid":"uid-2","creationTimestamp":"2026-10-02T00:00:00Z"},"spec":{"storageClassName":"fast"},"status":{"phase":"Pending"}}]}"""));

        var pvcs = await client.ListPvcsAsync("opensandbox", CancellationToken.None);

        Assert.Equal(2, pvcs.Count);
        var first = pvcs[0];
        Assert.Equal("opensandbox", first.Namespace);
        Assert.Equal("pvc-1", first.ClaimName);
        Assert.Equal("uid-1", first.Uid);
        Assert.Equal("Bound", first.Phase);
        Assert.Equal("standard", first.StorageClass);
        Assert.Equal("1Gi", first.RequestedStorage);
        Assert.Equal("1Gi", first.Capacity);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), first.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T00:00:00Z"), first.DeletionTimestamp);
        Assert.Equal("business", first.Labels[VolumeLabels.Lifecycle]);
        Assert.Equal("Pending", pvcs[1].Phase);
        Assert.Null(pvcs[1].DeletionTimestamp);
        Assert.Empty(pvcs[1].Labels);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/namespaces/opensandbox/persistentvolumeclaims",
            request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task ListPodMounts_ExcludesFinishedAndPodsWithoutClaims()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK,
            """{"apiVersion":"v1","kind":"PodList","items":[{"metadata":{"name":"pod-running"},"spec":{"volumes":[{"name":"data","persistentVolumeClaim":{"claimName":"pvc-1"}},{"name":"tmp","emptyDir":{}}]},"status":{"phase":"Running"}},{"metadata":{"name":"pod-pending"},"spec":{"volumes":[{"name":"data","persistentVolumeClaim":{"claimName":"pvc-2"}}]},"status":{"phase":"Pending"}},{"metadata":{"name":"pod-succeeded"},"spec":{"volumes":[{"name":"data","persistentVolumeClaim":{"claimName":"pvc-3"}}]},"status":{"phase":"Succeeded"}},{"metadata":{"name":"pod-failed"},"spec":{"volumes":[{"name":"data","persistentVolumeClaim":{"claimName":"pvc-4"}}]},"status":{"phase":"Failed"}},{"metadata":{"name":"pod-novolumes"},"spec":{"volumes":[]},"status":{"phase":"Running"}},{"metadata":{"name":"pod-emptydir"},"spec":{"volumes":[{"name":"tmp","emptyDir":{}}]},"status":{"phase":"Running"}}]}"""));

        var mounts = await client.ListPodMountsAsync("opensandbox", CancellationToken.None);

        Assert.Equal(2, mounts.Count);
        Assert.Equal("pod-running", mounts[0].PodName);
        Assert.Equal(["pvc-1"], mounts[0].ClaimNames);
        Assert.Equal("pod-pending", mounts[1].PodName);
        Assert.Equal(["pvc-2"], mounts[1].ClaimNames);
        Assert.Equal("/api/v1/namespaces/opensandbox/pods",
            Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task PatchPvcLabels_SendsMergePatch()
    {
        var (client, handler) = CreateClient(_ => Json(HttpStatusCode.OK,
            """{"apiVersion":"v1","kind":"PersistentVolumeClaim","metadata":{"namespace":"opensandbox","name":"pvc-1","uid":"uid-1"}}"""));

        await client.PatchPvcLabelsAsync("opensandbox", "pvc-1",
            new Dictionary<string, string> { [VolumeLabels.Lifecycle] = "business" },
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PATCH", request.Method);
        Assert.Equal("/api/v1/namespaces/opensandbox/persistentvolumeclaims/pvc-1",
            request.Uri.AbsolutePath);
        Assert.Equal("application/merge-patch+json", request.ContentType);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("business", body.RootElement.GetProperty("metadata")
            .GetProperty("labels").GetProperty(VolumeLabels.Lifecycle).GetString());
    }

    private static (KubernetesVolumeClient Client, CannedHandler Handler) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new CannedHandler(responder);
        var configuration = new KubernetesClientConfiguration { Host = "https://k8s.test" };
        var kubernetes = new Kubernetes(configuration, handler);
        return (new KubernetesVolumeClient(kubernetes, Options), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : DelegatingHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri!,
                request.Content?.Headers.ContentType?.MediaType,
                body));
            var response = responder(request);
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed record CapturedRequest(
        string Method,
        Uri Uri,
        string? ContentType,
        string? Body);
}
