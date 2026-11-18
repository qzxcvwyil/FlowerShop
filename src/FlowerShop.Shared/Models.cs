namespace FlowerShop.Shared;

public enum OrderStatus { Pending, Preparing, OutForDelivery, Delivered, Cancelled }

public sealed record ProductDto(int Id, string Name, string Description, decimal Price, string Category, string Emoji, int Stock);
public sealed record ProductUpsert(string Name, string Description, decimal Price, string Category, string Emoji, int Stock);
public sealed record CartLine(ProductDto Product, int Quantity) { public decimal Total => Product.Price * Quantity; }

public sealed record OrderItemRequest(int ProductId, int Quantity);
public sealed record CreateOrderRequest(string Address, string? Note, IReadOnlyList<OrderItemRequest> Items);
public sealed record OrderLineDto(int ProductId, string Name, int Quantity, decimal UnitPrice);
public sealed record OrderDto(int Id, DateTime CreatedUtc, OrderStatus Status, string Address, string CustomerName,
                              decimal Delivery, decimal Total, IReadOnlyList<OrderLineDto> Lines);
public sealed record UpdateStatusRequest(OrderStatus Status);

public sealed record RegisterRequest(string Email, string Password, string FullName);
public sealed record LoginRequest(string Email, string Password);
public sealed record AuthResponse(string Token, string Email, string FullName, string Role);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
