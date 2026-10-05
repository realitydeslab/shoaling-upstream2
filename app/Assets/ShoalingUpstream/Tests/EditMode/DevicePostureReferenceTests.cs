using NUnit.Framework;
using ShoalingUpstream.Experience;
using UnityEngine;

namespace ShoalingUpstream.Tests
{
    public sealed class DevicePostureReferenceTests
    {
        [Test]
        public void RawGravityDetectsInversionEvenIfCameraImageAutorotates()
        {
            var posture = new DevicePostureReference();
            posture.Calibrate(Vector3.left);
            Assert.That(posture.IsPutDown(Vector3.left), Is.False);
            Assert.That(posture.IsPutDown(Vector3.right), Is.True);
            Assert.That(posture.IsPutDown(Vector3.forward), Is.True);
            Assert.That(posture.IsPutDown(Quaternion.AngleAxis(55f, Vector3.up) * Vector3.left), Is.False);
        }
        [Test]
        public void MissingGravityDoesNotInventADevicePosture()
        {
            var posture = new DevicePostureReference();
            posture.Calibrate(Vector3.zero);
            Assert.That(posture.Calibrated, Is.False);
            Assert.That(posture.IsPutDown(Vector3.right), Is.False);
            posture.Calibrate(Vector3.down);
            Assert.That(posture.IsPutDown(Vector3.zero), Is.False);
        }
    }
}
