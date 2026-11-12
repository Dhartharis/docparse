export function catalogMappingSuggestions(headers: string[]) {
  const unique = (aliases: string[]) => {
    const matches = headers.filter(value => aliases.includes(value.trim().toLocaleLowerCase("en")));
    return matches.length === 1 ? matches[0] : "";
  };
  return {
    id: unique(["id", "sku", "code", "código", "codigo", "mã"]),
    name: unique(["name", "product name", "nombre", "producto", "tên sản phẩm", "tên"]),
    price: unique(["price", "selling price", "precio", "precio de venta", "giá", "giá bán"]),
    stock: unique(["stock", "available stock", "stock disponible", "disponible", "tồn kho"]),
    observed: unique(["updated", "updated at", "observed at", "actualizado", "fecha de actualización", "cập nhật"]),
  };
}
