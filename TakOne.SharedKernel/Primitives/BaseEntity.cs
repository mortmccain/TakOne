namespace TakOne.SharedKernel.Primitives;

public abstract class BaseEntity
{
    public Guid Id { get; protected set; }                  // what happens when we have Id in here and in the child class? which Id is used? 
                                                            // why is the set protected and not private?
    protected BaseEntity()
    {
        Id = Guid.NewGuid();
    }
    // for aggregate root
    protected BaseEntity(Guid id)
    {
        Id = id;
    }

    /*
     the Id in the child class will hide the Id in this (parent) class which will cause a compile warning and may cause issues. 
     the Id in the parent class is usable inside the chile class so just use that instead of making a new Id property
     */

    public override bool Equals(object? obj)    // why the "?" after object
    {
        if (obj is not BaseEntity other)       // don't this and the other if for getType mean the same?
            return false;
        if (ReferenceEquals(this, other))
            return true;
        if (GetType() != other.GetType())           // is the first GetType actually this.GetType?  and what does this part of the code even mean?
            return false;

        /*
         yes. yes it is.
         that part mean : Two entities are equal only if they are exactly the same type
         better question would be : how the FUCK (fuck would be necessary in this question) does the variable "other"  get introduced 
         in an if statement and still is recognized inside the other parts of the block that that if statement is in?
         the answer is that the "is not" will expand the scope of the "other" to the scope of the block that the if is in. yep

         The if (obj is not BaseEntity other) pattern does two things:

         Checks if obj can be cast to BaseEntity
         Declares variable other that's in scope for the rest of the method


        public override bool Equals(object obj)
        {
        // Try to pattern match
        if (obj is not BaseEntity other)
        return false;  // If we return here, we don't need 'other'

        // If we get here, 'other' is definitely assigned and in scope
        // because we didn't return at the guard clause

        Console.WriteLine(other.Id);  // Works!
        Console.WriteLine(other.GetType());  // Works!

        return Id == other.Id;  // Works throughout method
        }
         */

        if (Id == Guid.Empty || other.Id == Guid.Empty)
            return false;

        /*
         this alternative looks more clever and more optimized but it's not clever since it's not that much faster and it is harder
         to read which in turn makes it retarded:

        return Id != Guid.Empty && Id == other.Id;
         
         */

        return Id == other.Id;
    }

    // why override this? what would be the benefit? why not hash it where ever we need it instead if overrideing it here?

    /*
          we are going to use objects in some places that GetHashCode is needed for them. if we override equals we HAVE to override GetHashCode 
          as well becasue equal objects have equal hash codes 
     */

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public static bool operator ==(BaseEntity? left, BaseEntity? right)
    {
        if (left is null && right is null)
            return true;
        if (left is null || right is null)
            return false;
        return left.Equals(right);
    }

    public static bool operator !=(BaseEntity? left, BaseEntity? right)
    {
        return !(left == right);
    }
}