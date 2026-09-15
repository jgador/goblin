using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence.Entities;

[Table("work_items", Schema = "goblin")]
public partial class WorkItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("objective")]
    public string Objective { get; set; } = null!;
}
