using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using k8s.Models;

using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Events;
using KubeOps.Abstractions.Queue;
using KubeOps.KubernetesClient;
using KubeOps.Operator.Test.TestEntities;
using KubeOps.Transpiler;

using Microsoft.Extensions.DependencyInjection;

namespace KubeOps.Operator.Test.Events;

public class EventPublisherIntegrationTest : IntegrationTestBase, IAsyncLifetime
{
    private static readonly InvocationCounter<V1OperatorIntegrationTestEntity> Mock = new();
    private IKubernetesClient<V1OperatorIntegrationTestEntity> _client = null!;

    public EventPublisherIntegrationTest(HostBuilder hostBuilder, MlcProvider provider) : base(hostBuilder, provider)
    {
        Mock.Clear();
    }

    [Fact]
    public async Task Should_Create_New_Event()
    {
        const string eventName = "single-entity.default.REASON.message.Normal";
        var encodedEventName =
            Convert.ToHexString(
                SHA512.HashData(
                    Encoding.UTF8.GetBytes(eventName)));

        await _client.CreateAsync(new V1OperatorIntegrationTestEntity("single-entity", "username", "default"));
        await Mock.WaitForInvocations;

        var eventClient = _hostBuilder.Services.GetRequiredService<IKubernetesClient<Corev1Event>>();
        var e = await eventClient.GetAsync(encodedEventName, "default");
        e!.Count.Should().Be(1);
        e.Metadata.Annotations.Should().Contain(a => a.Key == "originalName" && a.Value == eventName);
    }

    [Fact]
    public async Task Should_Increase_Count_On_Existing_Event()
    {
        Mock.TargetInvocationCount = 5;
        const string eventName = "test-entity.default.REASON.message.Normal";
        var encodedEventName =
            Convert.ToHexString(
                SHA512.HashData(
                    Encoding.UTF8.GetBytes(eventName)));

        await _client.CreateAsync(new V1OperatorIntegrationTestEntity("test-entity", "username", "default"));
        await Mock.WaitForInvocations;

        var eventClient = _hostBuilder.Services.GetRequiredService<IKubernetesClient<Corev1Event>>();
        var e = await eventClient.GetAsync(encodedEventName, "default");
        e!.Count.Should().Be(5);
        e.Metadata.Annotations.Should().Contain(a => a.Key == "originalName" && a.Value == eventName);
    }

    public async Task InitializeAsync()
    {
        var meta = _mlc.ToEntityMetadata(typeof(V1OperatorIntegrationTestEntity)).Metadata;
        _client = new KubernetesClient<V1OperatorIntegrationTestEntity>(meta);
        await _hostBuilder.ConfigureAndStart(builder => builder.Services
            .AddSingleton(Mock)
            .AddKubernetesOperator()
            .AddController<TestController, V1OperatorIntegrationTestEntity>());
    }

    public async Task DisposeAsync()
    {
        await _client.DeleteAsync(await _client.ListAsync("default"));
        using var eventClient = _hostBuilder.Services.GetRequiredService<IKubernetesClient<Corev1Event>>();
        await eventClient.DeleteAsync(await eventClient.ListAsync("default"));
        _client.Dispose();
    }

    private class TestController : IEntityController<V1OperatorIntegrationTestEntity>
    {
        private readonly InvocationCounter<V1OperatorIntegrationTestEntity> _svc;
        private readonly EntityRequeue<V1OperatorIntegrationTestEntity> _requeue;
        private readonly EventPublisher _eventPublisher;

        public TestController(
            InvocationCounter<V1OperatorIntegrationTestEntity> svc,
            EntityRequeue<V1OperatorIntegrationTestEntity> requeue,
            EventPublisher eventPublisher)
        {
            _svc = svc;
            _requeue = requeue;
            _eventPublisher = eventPublisher;
        }

        public async Task ReconcileAsync(V1OperatorIntegrationTestEntity entity)
        {
            await _eventPublisher(entity, "REASON", "message");
            _svc.Invocation(entity);

            if (_svc.Invocations.Count < _svc.TargetInvocationCount)
            {
                _requeue(entity, TimeSpan.FromMilliseconds(10));
            }
        }
    }
}
