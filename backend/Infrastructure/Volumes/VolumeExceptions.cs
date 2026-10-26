namespace OpenSandbox.Web.Infrastructure.Volumes;

public sealed class VolumeDependencyException(string message) : Exception(message);

public sealed class VolumeConflictException(string message) : Exception(message);

public sealed class VolumeNotFoundException(string message) : Exception(message);

public sealed class VolumeValidationException(string message) : Exception(message);

public sealed class VolumeUnauthorizedException(string message) : Exception(message);
